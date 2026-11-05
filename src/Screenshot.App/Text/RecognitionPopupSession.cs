namespace Screenshot.App.Text;

/// <summary>Keeps one result surface alive across recognition and translation.</summary>
internal sealed class RecognitionPopupSession : IDisposable
{
    private readonly RecognitionResultPopupWindow _window;
    private bool _completed;
    private readonly CancellationTokenSource _cancellation = new();
    internal CancellationToken CancellationToken => _cancellation.Token;

    private RecognitionPopupSession(string title, string status, bool closeAfterCopy)
    {
        _window = new RecognitionResultPopupWindow(title, string.Empty, null, closeAfterCopy)
        { ShowActivated = false, Topmost = true };
        _window.ShowBusy(title, status);
        _window.Closed += OnClosed;
        _window.Show();
    }

    internal static RecognitionPopupSession? Begin(bool enabled, string title,
        string status = "本机识别 · 图片无需上传", bool closeAfterCopy = true) =>
        enabled ? new RecognitionPopupSession(title, status, closeAfterCopy) : null;

    internal void Update(string title, string status)
    {
        if (!_completed && !_window.WasClosed) _window.ShowBusy(title, status);
    }

    internal void Complete(string title, string source, string? translation = null, string? notice = null)
    {
        _completed = true;
        // Closing a pending popup dismisses it for this operation; do not reopen it.
        _window.ShowResult(title, source, translation, notice);
    }

    public void Dispose()
    {
        _window.Closed -= OnClosed;
        if (!_completed) _cancellation.Cancel();
        if (!_completed && !_window.WasClosed) _window.Close();
        _cancellation.Dispose();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (!_completed) _cancellation.Cancel();
    }
}
