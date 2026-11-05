namespace Screenshot.App.Capture;

// Owns one early frame until the overlay takes it. Window construction failure,
// Escape and late native completion all release the same frame exactly once.
internal sealed class PendingCaptureFrame : IDisposable
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Task<CapturedImage> _frame;
    private bool _taken;
    private bool _disposed;

    internal PendingCaptureFrame(Func<CancellationToken, Task<CapturedImage>> capture) =>
        _frame = capture(_cancellation.Token);

    internal async Task<CapturedImage> TakeAsync(CancellationToken cancellationToken)
    {
        var frame = await _frame.WaitAsync(cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_taken) throw new InvalidOperationException("The startup frame has already been taken.");
            _taken = true;
            return frame;
        }
    }

    public void Dispose()
    {
        bool release;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            release = !_taken;
        }
        _cancellation.Cancel();
        _ = ReleaseAsync(release);
    }

    private async Task ReleaseAsync(bool release)
    {
        try
        {
            var frame = await _frame.ConfigureAwait(false);
            if (release) frame.Dispose();
        }
        catch (Exception) { /* Startup reports errors; abandoned frames only need cleanup. */ }
        finally { _cancellation.Dispose(); }
    }
}
