namespace Screenshot.App.Capture;

/// <summary>One bounded preview worker; requests coalesce without losing the final update.</summary>
internal sealed class ScrollCapturePreviewPublisher(
    Func<Task> publishAsync,
    int minimumIntervalMilliseconds,
    CancellationToken cancellationToken,
    Action<Exception?>? statusChanged = null) : IAsyncDisposable
{
    private readonly object _sync = new();
    private Task _worker = Task.CompletedTask;
    private bool _running;
    private bool _pending;
    private bool _closed;
    private long _lastPublishedTimestamp;
    private int _adaptiveIntervalMilliseconds = Math.Max(0, minimumIntervalMilliseconds);
    internal Exception? LastError { get; private set; }

    internal void Request()
    {
        lock (_sync)
        {
            if (_closed || cancellationToken.IsCancellationRequested) return;
            _pending = true;
            if (_running) return;
            _running = true;
            // Always enter the worker, even on cancellation, so its lifetime is tracked.
            _worker = Task.Run(PublishPendingAsync);
        }
    }

    internal async Task RefreshAsync()
    {
        Request();
        Task worker;
        lock (_sync) worker = _worker;
        await worker.ConfigureAwait(false);
    }

    private async Task PublishPendingAsync()
    {
        try
        {
            while (true)
            {
                var delay = Math.Max(0, _adaptiveIntervalMilliseconds -
                    (Environment.TickCount64 - _lastPublishedTimestamp));
                if (delay > 0)
                    await Task.Delay((int)delay, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                lock (_sync) _pending = false;
                var started = Environment.TickCount64;
                var error = await TryPublishAsync(publishAsync, cancellationToken).ConfigureAwait(false);
                if (error is not null || LastError is not null) statusChanged?.Invoke(error);
                LastError = error;
                _adaptiveIntervalMilliseconds = Math.Max(minimumIntervalMilliseconds,
                    (int)Math.Min(800, (Environment.TickCount64 - started) * 2));
                _lastPublishedTimestamp = Environment.TickCount64;
                lock (_sync)
                {
                    if (_pending) continue;
                    _running = false;
                    return;
                }
            }
        }
        catch
        {
            lock (_sync)
            {
                _running = false;
                _closed |= cancellationToken.IsCancellationRequested;
            }
            throw;
        }
    }

    internal static async Task<Exception?> TryPublishAsync(Func<Task> publish, CancellationToken token)
    {
        for (var attempt = 0; ; attempt++)
        {
            token.ThrowIfCancellationRequested();
            try { await publish().ConfigureAwait(false); return null; }
            catch (Exception exception) when (!token.IsCancellationRequested)
            {
                // Preview pixels are expendable; captured content is not. A
                // transient GDI/render failure must not close the publisher or
                // turn a correctly stitched final image into a failed capture.
                if (attempt >= 2) return exception;
                await Task.Delay(100 * (attempt + 1), token).ConfigureAwait(false);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task worker;
        lock (_sync)
        {
            _closed = true;
            worker = _worker;
        }
        try { await worker.ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }
}
