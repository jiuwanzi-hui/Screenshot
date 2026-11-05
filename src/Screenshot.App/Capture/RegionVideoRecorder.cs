using System.IO;
using System.Globalization;
using ScreenRecorderLib;
using Screenshot.App.Core;
using WinForms = System.Windows.Forms;

namespace Screenshot.App.Capture;

internal enum RegionVideoRecorderState
{
    Ready,
    Starting,
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
    private readonly string _outputPath;
    private readonly RecordingSession _session;
    private readonly string? _encoderFallbackReason;
    private readonly RecordingErrorLog _errorLog = new();
    private readonly string _diagnosticContext;

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

        var profile = RecordingCompatibility.Resolve(deviceName);
        var health = new RecordingEncoderHealth(Path.Combine(AppMetadata.DiagnosticsDirectoryPath, "recording-encoder-health.json"));
        var displayIdentity = RecordingCompatibility.GetDisplayIdentity(deviceName);
        var encoderKey = RecordingEncoderHealth.CreateKey(displayIdentity, codec);
        _diagnosticContext = RecordingErrorLog.Describe(Region, codec, frameRate, recordSystemAudio, recordMicrophone) +
            $"\nDisplay: {deviceName}\nAdapter: {displayIdentity}";
        if (profile.HardwareEncoding && health.HasRecentFailure(encoderKey, deviceName, codec))
        {
            profile = profile with { HardwareEncoding = false };
            _encoderFallbackReason = "本显示器最近发生硬件编码缓冲或驱动错误，本次使用软件编码；保留原格式、分辨率、帧率和声音。";
        }
        var supportsCompatible = RecordingCompatibility.SupportsWindowsGraphicsCapture();
        _session = new RecordingSession(_outputPath, profile, supportsCompatible, selected =>
        {
            if (selected.UseWindowsGraphicsCapture && !supportsCompatible)
                throw new InvalidOperationException("当前会话不支持兼容屏幕采集，请在已连接的实体显示器上录制，并检查显示驱动。");
            return new NativeRecordingBackend(CreateOptions(deviceName, sourceRegion,
                recordSystemAudio, recordMicrophone, codec, frameRate, microphoneDeviceId, selected));
        });
        _session.Failed += error => _ = RecordFailureAsync("failed", error);
        _session.DiagnosticFailure += (stage, error) => _ = RecordFailureAsync(stage, error);
        _session.AttemptFailed += (failedProfile, error) =>
        {
            if (failedProfile.HardwareEncoding && RecordingEncoderHealth.IsEncoderFailure(error)) health.RememberFailure(encoderKey);
        };
    }

    internal static RecorderOptions CreateOptions(string deviceName, ScreenRegion sourceRegion,
        bool recordSystemAudio, bool recordMicrophone, VideoRecordingCodec codec, int frameRate,
        string? microphoneDeviceId, RecordingProfile profile)
    {
        var recordingSource = new DisplayRecordingSource(deviceName)
        {
            // Desktop Duplication uses ScreenRecorderLib's explicit cursor
            // compositor. WGC composites the desktop cursor into the captured
            // texture, which can expose a stale overlay cursor as a second
            // Hand/SizeAll pointer in the recording.
            RecorderApi = profile.UseWindowsGraphicsCapture
                ? RecorderApi.WindowsGraphicsCapture : RecorderApi.DesktopDuplication,
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
                IsVideoFramePreviewEnabled = false,
            },
            VideoEncoderOptions = new VideoEncoderOptions
            {
                Framerate = NormalizeFrameRate(frameRate),
                IsFixedFramerate = true,
                Encoder = codec == VideoRecordingCodec.H265
                    ? new H265VideoEncoder()
                    : new H264VideoEncoder(),
                IsHardwareEncodingEnabled = profile.HardwareEncoding,
                IsThrottlingDisabled = false,
                IsMp4FastStartEnabled = true,
                Quality = 75,
            },
            AudioOptions = CreateAudioOptions(
                recordSystemAudio,
                recordMicrophone,
                microphoneDeviceId),
        };
        return options;
    }

    public event Action<string>? Failed { add => _session.Failed += value; remove => _session.Failed -= value; }
    internal event Action<string>? StartupProgress { add => _session.StartupProgress += value; remove => _session.StartupProgress -= value; }
    internal string RecordingDescription => _session.Profile.Description;
    internal bool IsCompatibilityMode => !_session.Profile.HardwareEncoding;
    internal string? StartupFallbackReason => _session.StartupFallbackReason ?? _encoderFallbackReason;

    internal Task<RecordingErrorLogResult> RecordFailureAsync(string stage, string error, VideoRecordingPreferences? requested = null)
    {
        var context = _diagnosticContext + $"\nMode: {_session.Profile.Description}\n" +
            $"API: {(_session.Profile.UseWindowsGraphicsCapture ? "WGC" : "DesktopDuplication")}\nHardware encoding: {_session.Profile.HardwareEncoding}";
        if (requested is { } preferences)
            context += "\nRequested " + RecordingErrorLog.Describe(Region, preferences.Codec, preferences.FrameRate,
                preferences.RecordSystemAudio, preferences.RecordMicrophone);
        return _errorLog.WriteAsync(stage, context, error);
    }

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

    public ScreenRegion Region { get; }

    internal static RegionVideoAudioConfiguration ResolveAudioConfiguration(
        bool recordSystemAudio,
        bool recordMicrophone)
    {
        return new RegionVideoAudioConfiguration(
            // Reserve the audio track even when starting muted. The encoder
            // cannot add a new track after recording has begun. Disabled
            // sources do not capture audio; they can be enabled dynamically.
            true,
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

    public RegionVideoRecorderState State => _session.State;

    public void SetAudioSources(bool systemAudio, bool microphone) => _session.SetAudioSources(systemAudio, microphone);
    public void Start() => StartAsync().GetAwaiter().GetResult();
    public async Task StartAsync()
    {
        try { await _session.StartAsync().ConfigureAwait(false); }
        finally { await _errorLog.FlushAsync().ConfigureAwait(false); }
    }
    public void Pause() => _session.Pause();
    public void Resume() => _session.Resume();
    public Task<RegionVideoRecordingResult> StopAsync() => _session.StopAsync();
    internal bool TryTakeSnapshot(Stream output) => _session.TryTakeSnapshot(output);

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

    public void Dispose() => _session.Dispose();
    internal Task DisposeAsync() => Task.Run(_session.Dispose);

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

}
