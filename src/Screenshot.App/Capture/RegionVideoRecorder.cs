using System.IO;
using System.Globalization;
using System.Drawing;
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
    string? ErrorMessage,
    bool OpenEditor = false)
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
    private static readonly object WarmUpSync = new();
    private static Task? _warmUpTask;
    private readonly object _sync = new();
    private readonly string _outputPath;
    private readonly Recorder _recorder;
    private readonly TaskCompletionSource<RegionVideoRecordingResult> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _blackFrameDetectionCancellation = new();
    private string? _forcedFailureMessage;
    private bool _disposed;

    public RegionVideoRecorder(
        ScreenRegion region,
        string saveDirectory,
        bool recordSystemAudio = false,
        bool recordMicrophone = false,
        VideoRecordingCodec codec = VideoRecordingCodec.H264,
        int frameRate = 30,
        string? microphoneDeviceId = null)
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
            // WGC keeps cropped monitor recordings synchronized with desktop
            // compositor updates. Desktop Duplication can remain on its first
            // frame on some display and virtual-display drivers.
            RecorderApi = RecorderApi.WindowsGraphicsCapture,
            SourceRect = new ScreenRect(
                sourceRegion.X,
                sourceRegion.Y,
                sourceRegion.Width,
                sourceRegion.Height),
            IsCursorCaptureEnabled = true,
            IsBorderRequired = false,
            Position = new ScreenRecorderLib.ScreenPoint(0, 0),
            OutputSize = new ScreenSize(
                sourceRegion.Width,
                sourceRegion.Height),
            Stretch = StretchMode.Fill,
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
                recordMicrophone,
                microphoneDeviceId),
        };
        _recorder = Recorder.CreateRecorder(options);
        _recorder.OnRecordingComplete += OnRecordingComplete;
        _recorder.OnRecordingFailed += OnRecordingFailed;
    }

    public event Action<string>? Failed;

    internal static IReadOnlyList<RecordingDeviceOption> GetAudioInputDevices()
    {
        try
        {
            return Recorder.GetSystemAudioDevices(AudioDeviceSource.InputDevices)
                .Select(device => new RecordingDeviceOption(
                    device.DeviceName,
                    string.IsNullOrWhiteSpace(device.FriendlyName)
                        ? device.DeviceName
                        : device.FriendlyName))
                .Where(device => !string.IsNullOrWhiteSpace(device.Id))
                .ToArray();
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// Loads the native capture/encoder stack while the application is idle.
    /// Creating a recorder performs no capture and does not open a camera, but
    /// it avoids making the first user-triggered recording pay the driver and
    /// encoder initialization cost on the visible workflow.
    /// </summary>
    internal static Task WarmUpAsync(
        VideoRecordingCodec codec,
        int frameRate,
        bool recordSystemAudio,
        bool recordMicrophone,
        string? microphoneDeviceId)
    {
        lock (WarmUpSync)
        {
            return _warmUpTask ??= Task.Factory.StartNew(
                () => WarmUpCore(
                    codec,
                    frameRate,
                    recordSystemAudio,
                    recordMicrophone,
                    microphoneDeviceId),
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
        }
    }

    private static void WarmUpCore(
        VideoRecordingCodec codec,
        int frameRate,
        bool recordSystemAudio,
        bool recordMicrophone,
        string? microphoneDeviceId)
    {
        try
        {
            // Device enumeration itself loads the audio component without
            // touching an input stream.
            _ = GetAudioInputDevices();

            var screen = WinForms.Screen.AllScreens.FirstOrDefault();
            if (screen is null || screen.Bounds.Width < 2 || screen.Bounds.Height < 2)
            {
                return;
            }

            var warmUpDirectory = Path.Combine(
                Path.GetTempPath(),
                "SnapCut",
                "RecorderWarmup");
            var region = new ScreenRegion(
                screen.Bounds.X,
                screen.Bounds.Y,
                2,
                2);
            using var recorder = new RegionVideoRecorder(
                region,
                warmUpDirectory,
                recordSystemAudio,
                recordMicrophone,
                codec,
                frameRate,
                microphoneDeviceId);
        }
        catch
        {
            // Pre-warming is opportunistic. A driver may only initialize after
            // an interactive desktop is available; recording will still show
            // its normal actionable error if it cannot start later.
        }
    }

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
        bool recordMicrophone,
        string? microphoneDeviceId)
    {
        var configuration = ResolveAudioConfiguration(
            recordSystemAudio,
            recordMicrophone);
        return new AudioOptions
        {
            IsAudioEnabled = configuration.IsAudioEnabled,
            IsOutputDeviceEnabled = configuration.IsOutputDeviceEnabled,
            IsInputDeviceEnabled = configuration.IsInputDeviceEnabled,
            AudioInputDevice = microphoneDeviceId,
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
                _ = DetectPersistentBlackFramesAsync(
                    _blackFrameDetectionCancellation.Token);
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

    internal bool TryTakeSnapshot(Stream output)
    {
        ArgumentNullException.ThrowIfNull(output);
        lock (_sync)
        {
            return !_disposed && _recorder.TakeSnapshot(output);
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

    /// <summary>
    /// Starts the native recorder away from the WPF dispatcher. Desktop
    /// duplication, audio devices, and hardware encoders can block while the
    /// display driver is warming up after boot.
    /// </summary>
    public Task StartAsync()
    {
        return Task.Factory.StartNew(
            Start,
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    public async Task CancelAsync()
    {
        var result = await StopAsync();
        var path = string.IsNullOrWhiteSpace(result.FilePath)
            ? _outputPath
            : result.FilePath;
        for (var attempt = 0; attempt < 5 && File.Exists(path); attempt++)
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                if (attempt < 4)
                {
                    await Task.Delay(80);
                }
            }
            catch (UnauthorizedAccessException)
            {
                if (attempt < 4)
                {
                    await Task.Delay(80);
                }
            }
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
            _blackFrameDetectionCancellation.Cancel();
            _recorder.OnRecordingComplete -= OnRecordingComplete;
            _recorder.OnRecordingFailed -= OnRecordingFailed;
            _recorder.Dispose();
            _blackFrameDetectionCancellation.Dispose();
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
        string? forcedFailure;
        lock (_sync)
        {
            if (State == RegionVideoRecorderState.Failed)
            {
                return;
            }

            forcedFailure = _forcedFailureMessage;
            if (forcedFailure is not null)
            {
                // Complete outside this branch through the common failure path
                // so the partial file is removed and the UI receives one event.
            }
            else
            {
                State = RegionVideoRecorderState.Completed;
                _completion.TrySetResult(new RegionVideoRecordingResult(
                    string.IsNullOrWhiteSpace(e.FilePath) ? _outputPath : e.FilePath,
                    ErrorMessage: null));
            }
        }

        if (forcedFailure is not null)
        {
            CompleteWithFailure(forcedFailure);
        }
    }

    private void OnRecordingFailed(object? sender, RecordingFailedEventArgs e)
    {
        CompleteWithFailure(_forcedFailureMessage ?? e.Error);
    }

    private void CompleteWithFailure(string? errorMessage)
    {
        var message = string.IsNullOrWhiteSpace(errorMessage)
            ? "系统未能完成视频录制。"
            : errorMessage.Trim();
        var notify = false;
        lock (_sync)
        {
            if (State == RegionVideoRecorderState.Completed ||
                State == RegionVideoRecorderState.Failed)
            {
                return;
            }

            State = RegionVideoRecorderState.Failed;
            notify = _completion.TrySetResult(new RegionVideoRecordingResult(
                FilePath: null,
                ErrorMessage: message));
        }

        TryDeleteIncompleteFile();
        if (notify)
        {
            Failed?.Invoke(message);
        }
    }

    private async Task DetectPersistentBlackFramesAsync(
        CancellationToken cancellationToken)
    {
        const int requiredBlackSamples = 3;
        try
        {
            await Task.Delay(1000, cancellationToken);
            for (var sample = 0; sample < requiredBlackSamples; sample++)
            {
                lock (_sync)
                {
                    if (_disposed || State != RegionVideoRecorderState.Recording)
                    {
                        return;
                    }
                }

                using var snapshot = new MemoryStream();
                if (!_recorder.TakeSnapshot(snapshot) || snapshot.Length == 0)
                {
                    return;
                }

                snapshot.Position = 0;
                using var bitmap = new Bitmap(snapshot);
                if (!IsNearlyBlackFrame(bitmap))
                {
                    return;
                }

                if (sample + 1 < requiredBlackSamples)
                {
                    await Task.Delay(550, cancellationToken);
                }
            }

            StopForPersistentBlackFrames();
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            // Black-frame detection is protective only. Snapshot support must
            // never interrupt an otherwise valid recording.
        }
    }

    private void StopForPersistentBlackFrames()
    {
        const string message =
            "录制画面持续为黑色，可能是播放器硬件加速或受版权保护内容。" +
            "请关闭播放器硬件加速后重试；受保护的视频无法录制。";
        lock (_sync)
        {
            if (_disposed || State != RegionVideoRecorderState.Recording)
            {
                return;
            }

            _forcedFailureMessage = message;
            State = RegionVideoRecorderState.Stopping;
            try
            {
                _recorder.Stop();
            }
            catch
            {
                CompleteWithFailure(message);
            }
        }
    }

    internal static bool IsNearlyBlackFrame(Bitmap bitmap)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        if (bitmap.Width == 0 || bitmap.Height == 0)
        {
            return true;
        }

        const int samplesPerAxis = 12;
        var brightSamples = 0;
        var maximumLuminance = 0;
        for (var yIndex = 0; yIndex < samplesPerAxis; yIndex++)
        {
            var y = Math.Min(
                bitmap.Height - 1,
                (int)((yIndex + 0.5) * bitmap.Height / samplesPerAxis));
            for (var xIndex = 0; xIndex < samplesPerAxis; xIndex++)
            {
                var x = Math.Min(
                    bitmap.Width - 1,
                    (int)((xIndex + 0.5) * bitmap.Width / samplesPerAxis));
                var color = bitmap.GetPixel(x, y);
                var luminance = (color.R * 299 + color.G * 587 + color.B * 114) /
                                1000;
                maximumLuminance = Math.Max(maximumLuminance, luminance);
                if (luminance >= 10)
                {
                    brightSamples++;
                }
            }
        }

        // Be deliberately conservative: normal dark scenes and black-themed
        // applications contain controls, subtitles or highlights. Protected
        // capture surfaces are uniformly close to RGB(0,0,0).
        return maximumLuminance < 18 && brightSamples <= 1;
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
