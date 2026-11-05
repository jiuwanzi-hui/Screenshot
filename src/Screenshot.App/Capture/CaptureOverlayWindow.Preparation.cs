using System.Windows;
using System.Windows.Interop;

namespace Screenshot.App.Capture;

public partial class CaptureOverlayWindow
{
    private bool _isPreparedForResident;

    // Owned by one in-progress selection in the disposable feature process.
    // Measure the toolbar in place, retaining its theme/resource ancestry.
    // Create the hidden HWND as well: its first creation is substantial on
    // slower machines. It is never shown/activated and owns no desktop pixels.
    internal static CaptureOverlayWindow PrepareForResidentSelection(CaptureOverlayOptions options)
    {
        CaptureTimingDiagnostics.Mark("toolbar-prepare-constructor-start");
        var overlay = new CaptureOverlayWindow(options, isStartupPrewarm: true, deferInitialScreenCapture: true)
            { _isPreparedForResident = true };
        CaptureTimingDiagnostics.UiContext("toolbar-prepare-constructor-end", $"dpiX={overlay._overlayDpi.X} dpiY={overlay._overlayDpi.Y}");
        try
        {
            using var timing = CaptureTimingDiagnostics.Begin("toolbar-prepare-hidden-window");
            _ = new WindowInteropHelper(overlay).EnsureHandle();
            CaptureTimingDiagnostics.Mark("toolbar-prepare-hwnd-ready");
            overlay._nativeSelectionFrame.PrepareHiddenBorderWindows();
            CaptureTimingDiagnostics.Mark("toolbar-prepare-borders-ready");
            overlay.CaptureToolbar.Visibility = Visibility.Visible;
            // Measuring does not establish the final position. Keep this
            // first visible tree transparent until selection layout publishes it.
            overlay.CaptureToolbar.Opacity = 0;
            // The HWND is hidden. Keep the measured toolbar visible in this
            // tree instead of collapsing it and invalidating that work. The
            // screen/editor layout only becomes meaningful after adoption.
            overlay.CaptureToolbar.Measure(new System.Windows.Size(overlay.Width, overlay.Height));
            overlay.CaptureToolbar.Arrange(new Rect(overlay.CaptureToolbar.DesiredSize));
            return overlay;
        }
        catch { overlay.DiscardPreparedSession(); throw; }
    }

    internal bool CanCompletePreparedSession => _isPreparedForResident && !_isDisposed && !_isCompleted &&
        _virtualScreenBounds == VirtualScreen.GetBounds() && !IsVisible;

    internal void CompletePreparedSession(CaptureOverlayOptions options, CapturedImage snapshot)
    {
        using var adoptionTiming = CaptureTimingDiagnostics.Begin("toolbar-prepared-adopt");
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!CanCompletePreparedSession || options.InitialSelection is null || options.DeferInitialScreenCapture)
            throw new InvalidOperationException("截图准备状态已失效，请重新框选。");
        _options = options;
        _deferInitialScreenCapture = false;
        _initialInputPassthrough = false;
        _screenSnapshot = snapshot;
        FrozenScreenImage.Source = snapshot.Preview;
        Opacity = 1;
        ShowActivated = true;
        ConfigureResidentPresentation();
        // SourceInitialized already ran while this prepared HWND was hidden.
        AttachResidentPresentationCover(_windowHandle);
        PositionOverlayWindowTopmost();
        SetInitialInputPassthroughWindowStyle(enabled: false);
        _isPreparedForResident = false;
    }

    internal void DiscardPreparedSession()
    {
        if (_isCompleted || _isDisposed) return;
        // Cancelling preparation must not persist a tool selection or signal
        // that an unrelated active capture has completed.
        if (_isPreparedForResident) _options = null;
        Close();
    }
}
