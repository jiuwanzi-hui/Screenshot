using System.Runtime.InteropServices;

namespace Screenshot.App.Capture;

internal sealed class CaptureStartupFrameSource
{
    internal static CaptureStartupFrameSource Shared { get; } = new();
    private int _capturing;
    private int _compositionRefreshPending;
    private readonly Action _flushComposition;

    internal CaptureStartupFrameSource(Action? flushComposition = null) =>
        _flushComposition = flushComposition ?? FlushComposition;

    internal void RequestCompositionRefresh() =>
        Interlocked.Exchange(ref _compositionRefreshPending, 1);

    internal async Task<CapturedImage> CaptureAsync(Func<CancellationToken, CapturedImage> capture,
        CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (System.Windows.Application.Current is App application)
            await application.WaitForResidentCaptureChromeAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        // A timed-out driver call cannot safely be killed. Keep it bounded to
        // one worker; repeated shortcuts must not multiply GDI copies/CPU use.
        if (Interlocked.CompareExchange(ref _capturing, 1, 0) != 0)
            throw new InvalidOperationException("显示设备仍在结束上一次采集，请稍后重试。");
        Task<CapturedImage> work;
        try
        {
            work = Task.Factory.StartNew(() =>
            {
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    // Retire closed/minimized layered windows before the next
                    // first frame. This runs only on the bounded capture worker,
                    // with no UI/input wait, fixed delay or idle polling.
                    if (Interlocked.Exchange(ref _compositionRefreshPending, 0) != 0)
                    {
                        _flushComposition();
                        cancellationToken.ThrowIfCancellationRequested();
                    }
                    return capture(cancellationToken);
                }
                finally { Volatile.Write(ref _capturing, 0); }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }
        catch
        {
            Volatile.Write(ref _capturing, 0);
            throw;
        }
        try { return await work.WaitAsync(timeout ?? TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false); }
        catch
        {
            _ = Task.Run(() => DisposeLateFrameAsync(work));
            throw;
        }
    }

    internal static CapturedImage CaptureDesktop(CancellationToken token)
    {
        using var timing = CaptureTimingDiagnostics.Begin("keyboard-gdi-capture-worker");
        var bounds = VirtualScreen.GetBounds();
        CapturedImage captured;
        try { captured = ScreenCaptureService.CaptureIncludingLayeredWindows(bounds); }
        catch (System.ComponentModel.Win32Exception)
        {
            token.ThrowIfCancellationRequested();
            captured = ScreenCaptureService.Capture(bounds);
        }
        try
        {
            token.ThrowIfCancellationRequested();
            _ = captured.WarmPreview();
            return captured;
        }
        catch { captured.Dispose(); throw; }
    }

    private static async Task DisposeLateFrameAsync(Task<CapturedImage> work)
    {
        try { using var frame = await work.ConfigureAwait(false); }
        catch (Exception) { /* The original failure is reported by the session. */ }
    }

    private static void FlushComposition() => _ = DwmFlush();

    [DllImport("dwmapi.dll")]
    private static extern int DwmFlush();
}
