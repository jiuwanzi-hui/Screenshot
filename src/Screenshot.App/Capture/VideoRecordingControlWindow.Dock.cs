using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Screenshot.App.Infrastructure;
using Screenshot.App.Presentation;
using DrawingRectangle = System.Drawing.Rectangle;
using WinForms = System.Windows.Forms;

namespace Screenshot.App.Capture;

public partial class VideoRecordingControlWindow
{
    private const double CollapsedWidthDip = 120;
    private const double CollapsedHeightDip = 44;
    private Action? _finishToolbarTransition;

    private async void CollapseToolbarToEdge()
    {
        if (_isToolbarCollapsed || _isCompleting || !IsLoaded) return;
        FinishToolbarTransition();
        if (!MonitorGeometryService.TryGetWindowBounds(this, out var bounds)) return;

        RecordingDevicePopup.IsOpen = false;
        _expandedToolbarBounds = bounds;
        _expandedStatusVisibility = RecordingStatusPanel.Visibility;
        _expandedOptionsVisibility = RecordingOptionsPanel.Visibility;
        _expandedAnnotationVisibility = AnnotationToolsPanel.Visibility;
        _expandedActionsVisibility = RecordingActionPanel.Visibility;
        _isToolbarCollapsed = true;
        var dpi = VisualTreeHelper.GetDpi(this);
        var size = new DrawingRectangle(0, 0,
            (int)Math.Ceiling(CollapsedWidthDip * dpi.DpiScaleX),
            (int)Math.Ceiling(CollapsedHeightDip * dpi.DpiScaleY));
        // Dock on the toolbar's monitor, including a toolbar dragged away from
        // the recording monitor. Keep all controls and the timer on screen.
        var workArea = WinForms.Screen.FromRectangle(bounds).WorkingArea;
        var target = ConstrainDockBounds(size with
        {
            X = workArea.Right - size.Width - 8,
            Y = workArea.Bottom - size.Height - 8,
        }, workArea);
        var departure = ConstrainDockBounds(size with
        {
            X = bounds.Right - size.Width,
            Y = bounds.Top + (bounds.Height - size.Height) / 2,
        }, workArea);
        var version = ++_toolbarMoveAnimationVersion;
        _finishToolbarTransition = () => ApplyCollapsedToolbarLayout(target);

        if (UiTransitions.IsEnabled)
        {
            AnimateSurface(ControlSurface, ToolbarVisualScale, 1, 0.86, 1, 0.72, 1, 0, 90);
            await Task.Delay(90);
            if (version != _toolbarMoveAnimationVersion) return;
            ApplyCollapsedToolbarLayout(departure);
            AnimateSurface(CollapsedToolbarButton, CollapsedToolbarScale, 0.9, 1, 0.9, 1, 0.5, 1, 140);
            await AnimateToolbarPositionAsync(departure, target, version);
            if (version != _toolbarMoveAnimationVersion) return;
        }
        FinishToolbarTransition();
    }

    private async void ExpandToolbarFromEdge()
    {
        if (!_isToolbarCollapsed || _isCompleting) return;
        FinishToolbarTransition(preserveCollapsedPosition: true);
        var current = MonitorGeometryService.TryGetWindowBounds(this, out var bounds) ? bounds : _expandedToolbarBounds;
        _isToolbarCollapsed = false;
        _collapsedToolbarTopmostTimer.Stop();
        var target = ConstrainDockBounds(_expandedToolbarBounds,
            WinForms.Screen.FromRectangle(_expandedToolbarBounds).WorkingArea, 0);
        var arrival = current with
        {
            X = target.Right - current.Width,
            Y = target.Top + (target.Height - current.Height) / 2,
        };
        var version = ++_toolbarMoveAnimationVersion;
        _finishToolbarTransition = () => ApplyExpandedToolbarLayout(target);

        if (UiTransitions.IsEnabled)
        {
            await AnimateToolbarPositionAsync(current, arrival, version);
            if (version != _toolbarMoveAnimationVersion) return;
            ApplyExpandedToolbarLayout(target);
            AnimateSurface(ControlSurface, ToolbarVisualScale, 0.86, 1, 0.8, 1, 0, 1, 110);
            await Task.Delay(110);
            if (version != _toolbarMoveAnimationVersion) return;
        }
        FinishToolbarTransition();
    }

    private void ApplyCollapsedToolbarLayout(DrawingRectangle bounds)
    {
        SizeToContent = SizeToContent.Manual;
        ControlSurface.Visibility = Visibility.Collapsed;
        CollapsedToolbarButton.Visibility = Visibility.Visible;
        MinWidth = CollapsedWidthDip;
        MinHeight = CollapsedHeightDip;
        Width = CollapsedWidthDip;
        Height = CollapsedHeightDip;
        UpdateLayout();
        SetToolbarNativeBounds(bounds.X, bounds.Y, bounds.Width, bounds.Height);
        _collapsedToolbarTopmostTimer.Start();
    }

    private void ApplyExpandedToolbarLayout(DrawingRectangle bounds)
    {
        CollapsedToolbarButton.Visibility = Visibility.Collapsed;
        ControlSurface.Visibility = Visibility.Visible;
        RecordingStatusPanel.Visibility = _expandedStatusVisibility;
        RecordingOptionsPanel.Visibility = _expandedOptionsVisibility;
        AnnotationToolsPanel.Visibility = _expandedAnnotationVisibility;
        RecordingActionPanel.Visibility = _expandedActionsVisibility;
        MinHeight = 28;
        ApplyExpandedToolbarDimensions(_expandedAnnotationVisibility == Visibility.Visible);
        // Saved HWND bounds are pixels; WPF Width/Height are DIPs.
        var dpi = MonitorGeometryService.GetDpiScale(bounds);
        Width = bounds.Width / dpi.X;
        Height = bounds.Height / dpi.Y;
        SizeToContent = SizeToContent.Manual;
        UpdateLayout();
        SetToolbarNativeBounds(bounds.X, bounds.Y, bounds.Width, bounds.Height);
    }

    private void FinishToolbarTransition(bool preserveCollapsedPosition = false)
    {
        ++_toolbarMoveAnimationVersion;
        DrawingRectangle? current = preserveCollapsedPosition && _isToolbarCollapsed &&
            CollapsedToolbarButton.IsVisible && MonitorGeometryService.TryGetWindowBounds(this, out var bounds)
                ? bounds : null;
        var finish = _finishToolbarTransition;
        _finishToolbarTransition = null;
        finish?.Invoke();
        if (current is { } position)
            SetToolbarNativeBounds(position.X, position.Y, position.Width, position.Height);
        ResetSurfaceAnimation(ControlSurface, ToolbarVisualScale);
        ResetSurfaceAnimation(CollapsedToolbarButton, CollapsedToolbarScale);
    }

    private static void ResetSurfaceAnimation(FrameworkElement surface, ScaleTransform scale)
    {
        surface.BeginAnimation(OpacityProperty, null);
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        surface.Opacity = 1;
        scale.ScaleX = 1;
        scale.ScaleY = 1;
    }

    private static void AnimateSurface(FrameworkElement surface, ScaleTransform scale,
        double fromX, double toX, double fromY, double toY, double fromOpacity, double toOpacity, int milliseconds)
    {
        var duration = TimeSpan.FromMilliseconds(milliseconds);
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(fromX, toX, duration) { EasingFunction = easing });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(fromY, toY, duration) { EasingFunction = easing });
        surface.BeginAnimation(OpacityProperty,
            new DoubleAnimation(fromOpacity, toOpacity, duration) { EasingFunction = easing });
    }

    private async Task AnimateToolbarPositionAsync(DrawingRectangle from, DrawingRectangle to, int version)
    {
        var handle = new WindowInteropHelper(this).Handle;
        var stopwatch = Stopwatch.StartNew();
        while (UiTransitions.IsEnabled && stopwatch.Elapsed < ToolbarMoveAnimationDuration && version == _toolbarMoveAnimationVersion)
        {
            var progress = Math.Clamp(stopwatch.Elapsed.TotalMilliseconds / ToolbarMoveAnimationDuration.TotalMilliseconds, 0, 1);
            progress = 1 - Math.Pow(1 - progress, 3);
            // Travel at a fixed size. Resizing a layered HWND on every frame
            // rerenders the toolbar and makes remote pointer feedback lag.
            _ = NativeMethods.SetWindowPos(handle, new IntPtr(TopmostWindow),
                (int)Math.Round(from.X + (to.X - from.X) * progress),
                (int)Math.Round(from.Y + (to.Y - from.Y) * progress), 0, 0,
                DoNotResize | DoNotActivate | DoNotChangeOwnerZOrder);
            await Task.Delay(16);
        }
        if (version == _toolbarMoveAnimationVersion)
            SetToolbarNativeBounds(to.X, to.Y, to.Width, to.Height);
    }

    internal static DrawingRectangle ConstrainDockBounds(DrawingRectangle bounds, DrawingRectangle workArea, int inset = 4)
    {
        var width = Math.Min(bounds.Width, workArea.Width);
        var height = Math.Min(bounds.Height, workArea.Height);
        var marginX = Math.Min(inset, Math.Max(0, (workArea.Width - width) / 2));
        var marginY = Math.Min(inset, Math.Max(0, (workArea.Height - height) / 2));
        return new DrawingRectangle(
            Math.Clamp(bounds.X, workArea.Left + marginX, workArea.Right - width - marginX),
            Math.Clamp(bounds.Y, workArea.Top + marginY, workArea.Bottom - height - marginY), width, height);
    }

    private void SetToolbarNativeBounds(int x, int y, int width, int height)
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;
        _ = NativeMethods.SetWindowPos(handle, new IntPtr(TopmostWindow), x, y, width, height,
            DoNotActivate | DoNotChangeOwnerZOrder);
        Topmost = true;
    }

    private void OnCollapsedToolbarTopmostTimerTick(object? sender, EventArgs e)
    {
        if (!_isToolbarCollapsed || !IsLoaded) { _collapsedToolbarTopmostTimer.Stop(); return; }
        EnsureControlVisibleAndTopmost(restorePlacement: false);
    }
}
