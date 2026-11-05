using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Screenshot.App.Presentation;
using DrawingPoint = System.Drawing.Point;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;

namespace Screenshot.App.Capture;

public partial class CaptureOverlayWindow
{
    private CaptureAspectRatio _sessionAspectRatio;

    private bool CanChangeAspectRatio => _options is not null &&
        !_isScrollCaptureSelection && !_isCompleted && !_isActionInProgress &&
        !_isEditorInitializing && !_isSelecting && !_isMovingSelection &&
        !_isResizeDragging && InlineEditorCanvas.HasImage && HasValidSelection();

    private void OnAspectRatioClick(object sender, RoutedEventArgs e)
    {
        if (!CanChangeAspectRatio)
            return;

        ClearInlineToolSelection();
        AspectRatioButton.ContextMenu ??= CreateAspectRatioMenu();
        InlineActionAdjustments.Show(AspectRatioButton, () => InlineEditorCanvas);
        RefreshCaptureToolbarLayoutAfterOptionChange();
        e.Handled = true;
    }

    internal ContextMenu CreateAspectRatioMenu()
    {
        var menu = new ContextMenu();
        foreach (var ratio in Enum.GetValues<CaptureAspectRatio>())
        {
            var item = new MenuItem
            {
                Header = UiLocalization.Get(CaptureAspectRatioHelper.GetLabel(ratio)),
                Tag = ratio,
                IsCheckable = true,
                IsChecked = ratio == _sessionAspectRatio,
            };
            item.Click += async (_, _) => await ApplyAspectRatioAsync(ratio);
            menu.Items.Add(item);
        }
        ApplyThemedContextMenu(menu);
        menu.Opened += (_, _) =>
        {
            foreach (var item in menu.Items.OfType<MenuItem>())
                item.IsChecked = item.Tag is CaptureAspectRatio ratio && ratio == _sessionAspectRatio;
        };
        return menu;
    }

    internal async Task ApplyAspectRatioAsync(CaptureAspectRatio ratio)
    {
        if (!CanChangeAspectRatio || !Enum.IsDefined(ratio))
            return;

        InlineEditorCanvas.CommitPendingText();
        var previousBounds = GetPhysicalSelectionBounds();
        var protectedBounds = GetProtectedAnnotationBounds();
        if (!CaptureAspectRatioHelper.TryFitSelection(
                ToDrawingRectangle(previousBounds), ToDrawingRectangle(_virtualScreenBounds), ratio,
                protectedBounds is { } annotationBounds
                    ? ToDrawingRectangle(GetPhysicalSelectionBoundsForBounds(annotationBounds)) : null,
                out var fitted))
        {
            CaptureStatusMessage = UiLocalization.Get("当前标注无法完整放入此比例，请先调整标注位置。");
            CaptureStatusText.Visibility = Visibility.Visible;
            return;
        }

        if (!TryGetCaptureSurfacePoint(new DrawingPoint(fitted.Left, fitted.Top), out var topLeft) ||
            !TryGetCaptureSurfacePoint(new DrawingPoint(fitted.Right, fitted.Bottom), out var bottomRight))
            return;

        RememberAspectRatio(ratio);
        if (fitted == ToDrawingRectangle(previousBounds))
            return;

        var bounds = new Rect(topLeft, bottomRight);
        BeginSelectionAdjustment();
        UpdateSelectionBounds(bounds);
        await CompleteSelectionAdjustmentAsync();
        if (_isCompleted)
            return;

        UpdateSelectionBounds(bounds);
        UpdateNativeSelectionVisuals(bounds);
        SelectionRectangle.Visibility = Visibility.Collapsed;
        InlineEditorOutline.Visibility = Visibility.Visible;
        SetResizeControlsSuppressed(false);
        RestoreToolbarAfterNativeAdjustment(bounds);
        InlineEditorCanvas.Focus();
    }

    internal void RememberAspectRatio(CaptureAspectRatio ratio)
    {
        if (!Enum.IsDefined(ratio) || _sessionAspectRatio == ratio)
            return;

        _sessionAspectRatio = ratio;
        AspectRatioButton.ToolTip = UiLocalization.Get("普通截图比例") + " · " +
            UiLocalization.Get(CaptureAspectRatioHelper.GetLabel(ratio));
        // Publish before the unchanged-bounds return as well: choosing Free
        // or a ratio that already fits must still update the next capture.
        _options?.CaptureAspectRatioChanged?.Invoke(ratio);
    }
}
