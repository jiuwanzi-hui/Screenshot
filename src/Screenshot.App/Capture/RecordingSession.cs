using System.IO;

namespace Screenshot.App.Capture;

/// <summary>Serializes native calls and waits for actual startup, not merely Record() returning.</summary>
internal sealed class RecordingSession : IDisposable
{
    private readonly object _sync = new();
    private readonly Func<RecordingProfile, IRecordingBackend> _factory;
    private readonly string _outputPath;
    private readonly bool _allowCompatibleFallback;
    private readonly TimeSpan _startupTimeout;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly CancellationToken _lifetimeToken;
    private readonly TaskCompletionSource<RegionVideoRecordingResult> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Attempt? _attempt;
    private Task? _startTask;
    private bool _disposed;
    private volatile RegionVideoRecorderState _state = RegionVideoRecorderState.Ready;
    private RegionVideoAudioConfiguration? _pendingAudio;

    internal RecordingSession(string outputPath, RecordingProfile profile, bool allowCompatibleFallback,
        Func<RecordingProfile, IRecordingBackend> factory, TimeSpan? startupTimeout = null)
    {
        _outputPath = outputPath;
        Profile = profile;
        _allowCompatibleFallback = allowCompatibleFallback;
        _factory = factory;
        _startupTimeout = startupTimeout ?? TimeSpan.FromSeconds(30);
        _lifetimeToken = _lifetime.Token;
    }

    internal event Action<string>? Failed;
    internal event Action<string>? StartupProgress;
    internal event Action<RecordingProfile, string>? AttemptFailed;
    internal event Action<string, string>? DiagnosticFailure;
    internal RecordingProfile Profile { get; private set; }
    internal string? StartupFallbackReason { get; private set; }
    // UI status reads must never wait for a slow native call holding _sync.
    internal RegionVideoRecorderState State => _state;

    internal Task StartAsync()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_startTask is not null) return _startTask;
            if (_state != RegionVideoRecorderState.Ready) throw new InvalidOperationException("当前状态无法启动录制。");
            _state = RegionVideoRecorderState.Starting;
            return _startTask = Task.Run(StartCoreAsync);
        }
    }

    private async Task StartCoreAsync()
    {
        var errors = new List<string>();
        var profile = Profile;
        for (var attemptIndex = 0; attemptIndex < 2; attemptIndex++)
        {
            try
            {
                _lifetimeToken.ThrowIfCancellationRequested();
                StartupProgress?.Invoke(errors.Count == 0 ? $"正在启动{profile.Description}"
                    : "启动未成功，正在尝试兼容录制");
                Attempt attempt;
                lock (_sync)
                {
                    _lifetimeToken.ThrowIfCancellationRequested();
                    Profile = profile;
                    attempt = new Attempt(_factory(profile), this);
                    _attempt = attempt;
                    _lifetimeToken.ThrowIfCancellationRequested();
                    // Callbacks never wait for this lock. The native library may
                    // wait for its callback thread when Stop/Dispose is called.
                    attempt.Backend.Start(_outputPath);
                }
                await attempt.Started.Task.WaitAsync(_startupTimeout, _lifetimeToken).ConfigureAwait(false);
                lock (_sync)
                {
                    _lifetimeToken.ThrowIfCancellationRequested();
                    if (Volatile.Read(ref attempt.Error) is { } error) throw new InvalidOperationException(error);
                    if (_state != RegionVideoRecorderState.Starting) throw new OperationCanceledException();
                    _state = RegionVideoRecorderState.Recording;
                }
                return;
            }
            catch (OperationCanceledException) when (_lifetimeToken.IsCancellationRequested)
            {
                lock (_sync)
                {
                    ReleaseAttempt();
                    if (!_disposed) _state = RegionVideoRecorderState.Completed;
                    _completion.TrySetResult(new(null, null));
                }
                throw;
            }
            catch (Exception exception)
            {
                var detail = exception is TimeoutException ? "等待采集和编码器启动超时" : exception.Message;
                DiagnosticFailure?.Invoke($"startup-attempt-{attemptIndex + 1}", exception.ToString());
                AttemptFailed?.Invoke(profile, detail);
                errors.Add($"{profile.Description}：{detail}");
                // Destroy the failed instance before retrying. No retry after
                // recording has actually begun, so existing footage is preserved.
                try { lock (_sync) ReleaseAttempt(); }
                catch (Exception cleanupError)
                {
                    DiagnosticFailure?.Invoke("release-failed-attempt", cleanupError.ToString());
                    errors.Add($"释放录制器失败：{cleanupError.Message}");
                    break;
                }
                StartupFallbackReason = string.Join("；", errors);
                DeleteIncompleteFile();
                if (attemptIndex != 0 || !profile.HardwareEncoding) break;
                // Encoder failure does not require changing a working capture
                // API. Software encoding also works when WGC is unavailable.
                if (RecordingEncoderHealth.IsEncoderFailure(detail)) profile = profile with { HardwareEncoding = false };
                else if (_allowCompatibleFallback) profile = RecordingProfile.Compatible;
                else break;
            }
        }
        var message = string.Join("；", errors) + "。请检查显示/音频驱动；若使用虚拟屏，可在实体显示器上重试。";
        CompleteFailure(message);
        throw new InvalidOperationException(message);
    }

    internal void SetAudioSources(bool systemAudio, bool microphone)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_state == RegionVideoRecorderState.Paused)
            {
                _pendingAudio = RegionVideoRecorder.ResolveAudioConfiguration(systemAudio, microphone);
                return;
            }
            if (_state != RegionVideoRecorderState.Recording) throw new InvalidOperationException("当前录制状态无法更改声音。");
            _attempt!.Backend.SetAudioSources(systemAudio, microphone);
        }
    }

    internal void Pause()
    {
        lock (_sync)
        {
            if (_disposed || _state != RegionVideoRecorderState.Recording) return;
            _attempt!.Backend.Pause();
            _state = RegionVideoRecorderState.Paused;
        }
    }

    internal void Resume()
    {
        lock (_sync)
        {
            if (_disposed || _state != RegionVideoRecorderState.Paused) return;
            _attempt!.Backend.Resume();
            _state = RegionVideoRecorderState.Recording;
            if (_pendingAudio is { } audio)
            {
                _attempt.Backend.SetAudioSources(audio.IsOutputDeviceEnabled, audio.IsInputDeviceEnabled);
                _pendingAudio = null;
            }
        }
    }

    internal bool TryTakeSnapshot(Stream output)
    {
        ArgumentNullException.ThrowIfNull(output);
        lock (_sync)
        {
            return !_disposed && _state is (RegionVideoRecorderState.Recording or RegionVideoRecorderState.Paused)
                && _attempt!.Backend.TakeSnapshot(output);
        }
    }

    internal async Task<RegionVideoRecordingResult> StopAsync()
    {
        if (_state == RegionVideoRecorderState.Starting)
        {
            try { _lifetime.Cancel(); }
            catch (ObjectDisposedException) { }
        }
        // Even the synchronous part of native Stop can wait for a driver.
        await Task.Run(() =>
        {
            lock (_sync)
            {
                if (_disposed) return;
                if (_state == RegionVideoRecorderState.Ready)
                {
                    _state = RegionVideoRecorderState.Completed;
                    _completion.TrySetResult(new(null, null));
                }
                else if (_state == RegionVideoRecorderState.Starting) _lifetime.Cancel();
                else if (_state is RegionVideoRecorderState.Recording or RegionVideoRecorderState.Paused)
                {
                    _state = RegionVideoRecorderState.Stopping;
                    try { _attempt!.Backend.Stop(); }
                    catch (Exception e) { DiagnosticFailure?.Invoke("stop", e.ToString()); CompleteFailure(e.Message); }
                }
            }
        }).ConfigureAwait(false);
        try { return await _completion.Task.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            CompleteFailure("结束录制超时，请检查系统媒体组件和显示/音频驱动。");
            return await _completion.Task.ConfigureAwait(false);
        }
    }

    private void OnCompleted(Attempt attempt, string? path)
    {
        lock (_sync)
        {
            if (_disposed || !ReferenceEquals(attempt, _attempt) || _state == RegionVideoRecorderState.Failed) return;
            if (_state == RegionVideoRecorderState.Starting) return;
            _state = RegionVideoRecorderState.Completed;
            _completion.TrySetResult(new(string.IsNullOrWhiteSpace(path) ? _outputPath : path, null));
        }
    }

    private void OnFailed(Attempt attempt, string error)
    {
        lock (_sync)
        {
            if (_disposed || !ReferenceEquals(attempt, _attempt) || _state == RegionVideoRecorderState.Starting) return;
            if (_state is RegionVideoRecorderState.Failed or RegionVideoRecorderState.Completed) return;
            AttemptFailed?.Invoke(Profile, error);
            DiagnosticFailure?.Invoke("recording", error);
            if (Profile.HardwareEncoding && RecordingEncoderHealth.IsEncoderFailure(error))
                error = "硬件视频编码失败（编码缓冲或驱动异常）。再次开始录制将自动使用软件编码，保持所选格式、分辨率、帧率和声音。已产生的视频文件保留在录屏目录。原始错误：" + error;
            CompleteFailure(error);
        }
    }

    private void CompleteFailure(string message)
    {
        lock (_sync)
        {
            if (_disposed || _state is RegionVideoRecorderState.Completed or RegionVideoRecorderState.Failed) return;
            _state = RegionVideoRecorderState.Failed;
            _completion.TrySetResult(new(null, message));
        }
        Failed?.Invoke(message);
    }

    // Only called while holding _sync; no TakeSnapshot/Start/Stop can overlap.
    private void ReleaseAttempt()
    {
        var previous = _attempt;
        _attempt = null;
        previous?.Dispose();
    }

    internal void DeleteIncompleteFile()
    {
        try { File.Delete(_outputPath); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _lifetime.Cancel();
            _completion.TrySetResult(new(null, "录制器已关闭。"));
            try { ReleaseAttempt(); }
            finally { _lifetime.Dispose(); }
        }
    }

    private sealed class Attempt : IDisposable
    {
        internal readonly IRecordingBackend Backend;
        internal readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal string? Error;
        private readonly RecordingSession _owner;
        internal Attempt(IRecordingBackend backend, RecordingSession owner)
        {
            Backend = backend;
            _owner = owner;
            backend.Started += OnStarted;
            backend.Completed += OnCompleted;
            backend.Failed += OnFailed;
        }
        private void OnStarted() => Started.TrySetResult();
        private void OnCompleted(string? path)
        {
            const string earlyCompletion = "录制器在启动完成前已结束。";
            Volatile.Write(ref Error, earlyCompletion);
            Started.TrySetException(new InvalidOperationException(earlyCompletion));
            _ = Task.Run(() => _owner.OnCompleted(this, path));
        }
        private void OnFailed(string error)
        {
            Volatile.Write(ref Error, error);
            Started.TrySetException(new InvalidOperationException(error));
            _ = Task.Run(() => _owner.OnFailed(this, error));
        }
        public void Dispose()
        {
            Backend.Started -= OnStarted;
            Backend.Completed -= OnCompleted;
            Backend.Failed -= OnFailed;
            Backend.Dispose();
        }
    }
}
