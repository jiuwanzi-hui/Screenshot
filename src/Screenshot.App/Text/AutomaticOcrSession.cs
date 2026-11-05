using Screenshot.App.Capture;

namespace Screenshot.App.Text;

/// <summary>UI-owned, latest-only automatic OCR; snapshots follow the quiet period.</summary>
internal sealed class AutomaticOcrSession : IDisposable
{
    // Engines adapt their own thread budgets. Several pins must not start
    // several automatic native jobs at once and compete with foreground input.
    private static readonly SemaphoreSlim AutomaticWork = new(1, 1);
    private CancellationTokenSource? _pending;
    private bool _disposed;
    internal Task Completion { get; private set; } = Task.CompletedTask;

    internal void Schedule(Func<CapturedImage?> snapshot,
        Func<CapturedImage, CancellationToken, Task<OcrRecognitionResult>> recognize,
        Action<OcrRecognitionResult> publish, Action? started = null, int delayMilliseconds = 350)
    {
        if (_disposed) return;
        Cancel();
        var request = new CancellationTokenSource();
        _pending = request;
        Completion = RunAsync(request, snapshot, recognize, publish, started, delayMilliseconds);
    }

    internal void Cancel()
    {
        var pending = _pending;
        _pending = null;
        pending?.Cancel();
    }

    private async Task RunAsync(CancellationTokenSource request, Func<CapturedImage?> snapshot,
        Func<CapturedImage, CancellationToken, Task<OcrRecognitionResult>> recognize,
        Action<OcrRecognitionResult> publish, Action? started, int delayMilliseconds)
    {
        var token = request.Token;
        var acquired = false;
        try
        {
            await Task.Delay(delayMilliseconds, token);
            await AutomaticWork.WaitAsync(token);
            acquired = true;
            token.ThrowIfCancellationRequested();
            using var image = snapshot();
            if (image is null) return;
            started?.Invoke();
            var result = await recognize(image, token);
            token.ThrowIfCancellationRequested();
            publish(result);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception)
        {
            if (!token.IsCancellationRequested)
                publish(OcrRecognitionResult.Failure("自动识别失败，可点击“文”重试或检查内容识别设置。"));
        }
        finally
        {
            if (acquired) AutomaticWork.Release();
            if (ReferenceEquals(_pending, request)) _pending = null;
            request.Dispose();
        }
    }

    public void Dispose()
    {
        _disposed = true;
        Cancel();
    }
}
