using System.IO;
using ScreenRecorderLib;

namespace Screenshot.App.Capture;

internal interface IRecordingBackend : IDisposable
{
    event Action? Started;
    event Action<string?>? Completed;
    event Action<string>? Failed;
    void Start(string path);
    void Stop();
    void Pause();
    void Resume();
    void SetAudioSources(bool systemAudio, bool microphone);
    bool TakeSnapshot(Stream output);
}

internal sealed class NativeRecordingBackend : IRecordingBackend
{
    private readonly Recorder _recorder;
    private int _started;

    internal NativeRecordingBackend(RecorderOptions options)
    {
        _recorder = Recorder.CreateRecorder(options);
        _recorder.OnFrameRecorded += OnFrameRecorded;
        _recorder.OnRecordingComplete += OnCompleted;
        _recorder.OnRecordingFailed += OnFailed;
    }

    public event Action? Started;
    public event Action<string?>? Completed;
    public event Action<string>? Failed;
    private void OnFrameRecorded(object? sender, FrameRecordedEventArgs e)
    {
        // ScreenRecorderLib 6.6 raises Recording before RenderFrame. Its frame
        // callback is after successful RenderFrame/WriteSample, without needing
        // pixel previews, snapshots, a startup delay or a black-frame heuristic.
        if (Interlocked.Exchange(ref _started, 1) == 0) Started?.Invoke();
    }
    private void OnCompleted(object? sender, RecordingCompleteEventArgs e) => Completed?.Invoke(e.FilePath);
    private void OnFailed(object? sender, RecordingFailedEventArgs e) => Failed?.Invoke(e.Error);
    public void Start(string path) => _recorder.Record(path);
    public void Stop() => _recorder.Stop();
    public void Pause() => _recorder.Pause();
    public void Resume() => _recorder.Resume();
    public bool TakeSnapshot(Stream output) => _recorder.TakeSnapshot(output);
    public void SetAudioSources(bool systemAudio, bool microphone)
    {
        if (!_recorder.GetDynamicOptionsBuilder().SetDynamicAudioOptions(new DynamicAudioOptions
        {
            IsOutputDeviceEnabled = systemAudio,
            IsInputDeviceEnabled = microphone,
        }).Apply()) throw new InvalidOperationException("声音切换未生效，请重试。");
    }
    public void Dispose()
    {
        _recorder.OnFrameRecorded -= OnFrameRecorded;
        _recorder.OnRecordingComplete -= OnCompleted;
        _recorder.OnRecordingFailed -= OnFailed;
        _recorder.Dispose();
    }
}
