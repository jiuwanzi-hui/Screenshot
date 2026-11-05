using Screenshot.App.Capture;

namespace Screenshot.App.Text.Realtime;

internal sealed record RealtimeFrame(CapturedImage Image, ScreenRegion Bounds) : IDisposable
{
    public void Dispose() => Image.Dispose();
}

// A slow OCR operation may own one frame; only the newest waiting frame survives.
internal sealed class LatestFrameMailbox : IDisposable
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _ready = new(0, 1);
    private RealtimeFrame? _latest;
    private bool _disposed;

    internal void Publish(RealtimeFrame frame)
    {
        lock (_gate)
        {
            if (_disposed) { frame.Dispose(); return; }
            var replaced = _latest;
            _latest = frame;
            replaced?.Dispose();
            if (_ready.CurrentCount == 0) _ready.Release();
        }
    }

    internal async Task<RealtimeFrame> TakeAsync(CancellationToken token)
    {
        while (true)
        {
            await _ready.WaitAsync(token).ConfigureAwait(false);
            lock (_gate)
            {
                if (_disposed) throw new OperationCanceledException(token);
                if (_latest is not { } frame) continue;
                _latest = null;
                return frame;
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _latest?.Dispose();
            _latest = null;
            _ready.Dispose();
        }
        // The owner cancels and joins both loops before disposing the mailbox.
    }
}
