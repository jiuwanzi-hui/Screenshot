using System.IO;
using System.Globalization;
using ScreenRecorderLib;
using Screenshot.App.Core;
using WinForms = System.Windows.Forms;

namespace Screenshot.App.Capture;

internal enum RegionVideoRecorderState
{
    Ready,
    Recording,
    Paused,
    Stopping,
    Completed,
    Failed,
}

internal sealed record RegionVideoRecordingResult(
    string? FilePath,
    string? ErrorMessage)
{
    public bool IsSuccess =>
        !string.IsNullOrWhiteSpace(FilePath) &&
        string.IsNullOrWhiteSpace(ErrorMessage);
}

internal readonly record struct RegionVideoAudioConfiguration(
    bool IsAudioEnabled,
    bool IsOutputDeviceEnabled,
    bool IsInputDeviceEnabled);

internal sealed class RegionVideoRecorder : IDisposable
{
    private readonly object _sync = new();
    private readonly string _outputPath;
    private readonly Recorder _recorder;
    private readonly TaskCompletionSource<RegionVideoRecordingResult> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _disposed;

    public RegionVideoRecorder(
        ScreenRegion region,
        string saveDirectory,
        bool recordSystemAudio = false,
        bool recordMicrophone = false,
        VideoRecordingCodec codec = VideoRecordingCodec.H264,
        int frameRate = 30)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(saveDirectory);

        if (!TryResolveRecordingTarget(
                region,
                out var normalizedRegion,
                out var deviceName,
                out var sourceRegion))
        {
            throw new ArgumentException(
                "区域录制暂不支持跨屏框选，请在同一块屏幕内重新框选。",
                nameof(region));
        }

        Region = normalizedRegion;
        Directory.CreateDirectory(saveDirectory);
        _outputPath = CreateOutputPath(saveDirectory);

        var recordingSource = new DisplayRecordingSource(deviceName)
        {
            SourceRect = new ScreenRect(
                sourceRegion.X,
                sourceRegion.Y,
                sourceRegion.Width,
                sourceRegion.Height),
            IsCursorCaptureEnabled = true,
            IsBorderRequired = false,
        };
        var options = new RecorderOptions
        {
            SourceOptions = new SourceOptions
            {
                RecordingSources = [recordingSource],
            },
            OutputOptions = new OutputOptions
            {
                RecorderMode = RecorderMode.Video,
            },
            VideoEncoderOptions = new VideoEncoderOptions
            {
                Framerate = NormalizeFrameRate(frameRate),
                IsFixedFramerate = true,
                Encoder = codec == VideoRecordingCodec.H265
                    ? new H265VideoEncoder()
                    : new H264VideoEncoder(),
                IsHardwareEncodingEnabled = true,
                IsMp4FastStartEnabled = true,
                Quality = 75,
            },
            AudioOptions = CreateAudioOptions(
                recordSystemAudio,
                recordMicrophone),
        };

        _recorder = Recorder.CreateRecorder(options);
        _recorder.OnRecordingComplete += OnRecordingComplete;
        _recorder.OnRecordingFailed += OnRecordingFailed;
    }

    public event Action<string>? Failed;

    public ScreenRegion Region { get; }

    internal static RegionVideoAudioConfiguration ResolveAudioConfiguration(
        bool recordSystemAudio,
        bool recordMicrophone)
    {
        return new RegionVideoAudioConfiguration(
            recordSystemAudio || recordMicrophone,
            recordSystemAudio,
            recordMicrophone);
    }

    internal static int NormalizeFrameRate(int frameRate)
    {
        return frameRate is 24 or 30 or 60 ? frameRate : 30;
    }

    private static AudioOptions CreateAudioOptions(
        bool recordSystemAudio,
        bool recordMicrophone)
    {
        var configuration = ResolveAudioConfiguration(
            recordSystemAudio,
            recordMicrophone);
        return new AudioOptions
        {
            IsAudioEnabled = configuration.IsAudioEnabled,
            IsOutputDeviceEnabled = configuration.IsOutputDeviceEnabled,
            IsInputDeviceEnabled = configuration.IsInputDeviceEnabled,
        };
    }

    public RegionVideoRecorderState State { get; private set; } =
        RegionVideoRecorderState.Ready;

    public void Start()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (State != RegionVideoRecorderState.Ready)
            {
                return;
            }

            try
            {
                _recorder.Record(_outputPath);
                State = RegionVideoRecorderState.Recording;
            }
            catch (Exception exception)
            {
                CompleteWithFailure(exception.Message);
                throw;
            }
        }
    }

    public void Pause()
    {
        lock (_sync)
        {
            if (_disposed || State != RegionVideoRecorderState.Recording)
            {
                return;
            }

            _recorder.Pause();
            State = RegionVideoRecorderState.Paused;
        }
    }

    public void Resume()
    {
        lock (_sync)
        {
            if (_disposed || State != RegionVideoRecorderState.Paused)
            {
                return;
            }

            _recorder.Resume();
            State = RegionVideoRecorderState.Recording;
        }
    }

    public async Task<RegionVideoRecordingResult> StopAsync()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return new RegionVideoRecordingResult(
                    FilePath: null,
                    ErrorMessage: "录制器已关闭。");
            }

            if (State == RegionVideoRecorderState.Ready)
            {
                return new RegionVideoRecordingResult(null, null);
            }

            if (State is RegionVideoRecorderState.Completed or
                RegionVideoRecorderState.Failed)
            {
                return _completion.Task.GetAwaiter().GetResult();
            }

            if (State != RegionVideoRecorderState.Stopping)
            {
                State = RegionVideoRecorderState.Stopping;
                try
                {
                    _recorder.Stop();
                }
                catch (Exception exception)
                {
                    CompleteWithFailure(exception.Message);
                }
            }
        }

        try
        {
            return await _completion.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }
        catch (TimeoutException)
        {
            CompleteWithFailure("结束录制超时，请检查系统媒体组件是否可用。");
            return await _completion.Task;
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _recorder.OnRecordingComplete -= OnRecordingComplete;
            _recorder.OnRecordingFailed -= OnRecordingFailed;
            _recorder.Dispose();
        }
    }

    internal static bool TryResolveRecordingTarget(
        ScreenRegion region,
        out ScreenRegion normalizedRegion,
        out string deviceName,
        out ScreenRegion sourceRegion)
    {
        normalizedRegion = default;
        deviceName = string.Empty;
        sourceRegion = default;
        if (region.Width < 2 || region.Height < 2)
        {
            return false;
        }

        var evenRegion = region with
        {
            Width = region.Width & ~1,
            Height = region.Height & ~1,
        };
        foreach (var screen in WinForms.Screen.AllScreens)
        {
            var bounds = screen.Bounds;
            if (evenRegion.X < bounds.Left ||
                evenRegion.Y < bounds.Top ||
                evenRegion.X + evenRegion.Width > bounds.Right ||
                evenRegion.Y + evenRegion.Height > bounds.Bottom)
            {
                continue;
            }

            normalizedRegion = evenRegion;
            deviceName = screen.DeviceName;
            sourceRegion = new ScreenRegion(
                evenRegion.X - bounds.Left,
                evenRegion.Y - bounds.Top,
                evenRegion.Width,
                evenRegion.Height);
            return true;
        }

        return false;
    }

    private static string CreateOutputPath(string saveDirectory)
    {
        var timestamp = DateTime.Now.ToString(
            "yyyyMMdd-HHmmss",
            CultureInfo.InvariantCulture);
        for (var suffix = 0; ; suffix++)
        {
            var suffixText = suffix == 0 ? string.Empty : $"-{suffix}";
            var path = Path.Combine(
                saveDirectory,
                $"SnapCut-{timestamp}{suffixText}.mp4");
            if (!File.Exists(path))
            {
                return path;
            }
        }
    }

    private void OnRecordingComplete(object? sender, RecordingCompleteEventArgs e)
    {
        lock (_sync)
        {
            State = RegionVideoRecorderState.Completed;
            _completion.TrySetResult(new RegionVideoRecordingResult(
                string.IsNullOrWhiteSpace(e.FilePath) ? _outputPath : e.FilePath,
                ErrorMessage: null));
        }
    }

    private void OnRecordingFailed(object? sender, RecordingFailedEventArgs e)
    {
        CompleteWithFailure(e.Error);
    }

    private void CompleteWithFailure(string? errorMessage)
    {
        var message = string.IsNullOrWhiteSpace(errorMessage)
            ? "系统未能完成视频录制。"
            : errorMessage.Trim();
        lock (_sync)
        {
            State = RegionVideoRecorderState.Failed;
            _completion.TrySetResult(new RegionVideoRecordingResult(
                FilePath: null,
                ErrorMessage: message));
        }

        TryDeleteIncompleteFile();
        Failed?.Invoke(message);
    }

    private void TryDeleteIncompleteFile()
    {
        try
        {
            if (File.Exists(_outputPath))
            {
                File.Delete(_outputPath);
            }
        }
        catch
        {
        }
    }
}
