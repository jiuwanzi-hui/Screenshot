namespace Screenshot.App.Capture;

/// <summary>
/// Keeps the selected scroll-capture area visible while allowing pointer input to
/// continue to the target window.
/// </summary>
public sealed class ScrollCaptureSelection : IDisposable
{
    private readonly CaptureOverlayWindow _overlay;
    private ScreenRegion _captureRegion;
    private int _disposed;

    internal ScrollCaptureSelection(
        CaptureOverlayWindow overlay,
        ScreenRegion captureRegion)
    {
        _overlay = overlay;
        _captureRegion = captureRegion;
    }

    public event Action<ScreenRegion>? CaptureRegionChanged;

    public event Action? CancelRequested;

    public ScreenRegion CaptureRegion => _captureRegion;

    internal System.Windows.Window OverlayWindow => _overlay;

    internal void UpdateCaptureRegion(ScreenRegion captureRegion)
    {
        if (_captureRegion == captureRegion)
        {
            return;
        }

        _captureRegion = captureRegion;
        CaptureRegionChanged?.Invoke(captureRegion);
    }

    internal void RequestCancel()
    {
        try
        {
            CancelRequested?.Invoke();
        }
        catch
        {
            // Cancellation must never leave the low-level mouse hook installed.
        }
    }

    public CapturedImage CaptureSnapshot()
    {
        return _overlay.CaptureScrollSelectionSnapshot();
    }

    public Task LockForScrollingAsync(
        CancellationToken cancellationToken = default)
    {
        return _overlay.LockScrollCaptureSelectionAsync(cancellationToken);
    }

    /// <summary>
    /// Temporarily hides or restores the full overlay so native hit testing can
    /// resolve the real window under the selected region.
    /// </summary>
    public Task SetVisibleAsync(
        bool isVisible,
        CancellationToken cancellationToken = default)
    {
        return _overlay.SetScrollCaptureSelectionVisibleAsync(
            isVisible,
            cancellationToken);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _overlay.CloseScrollCaptureSelection();
    }
}
