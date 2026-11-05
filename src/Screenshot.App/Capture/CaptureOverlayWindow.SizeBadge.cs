using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DrawingColor = System.Drawing.Color;
using DrawingRectangle = System.Drawing.Rectangle;
using WpfPoint = System.Windows.Point;
using WpfSize = System.Windows.Size;

namespace Screenshot.App.Capture;

public partial class CaptureOverlayWindow
{
    private void SyncNativeSizeBadgeTheme()
    {
        var (background, backgroundEnd) = GetBadgeColors("AppSizeBadgeBackgroundBrush", DrawingColor.FromArgb(244, 248, 252));
        var (border, _) = GetBadgeColors("AppBorderBrush", DrawingColor.FromArgb(184, 196, 210));
        var (text, _) = GetBadgeColors("AppControlForegroundBrush", DrawingColor.FromArgb(48, 58, 74));
        _nativeSelectionSize.SetThemeColors(background, backgroundEnd, border, text);
    }

    private (DrawingColor Start, DrawingColor End) GetBadgeColors(string key, DrawingColor fallback)
    {
        static DrawingColor Opaque(System.Windows.Media.Color color) => DrawingColor.FromArgb(color.R, color.G, color.B);
        return TryFindResource(key) switch
        {
            SolidColorBrush brush => (Opaque(brush.Color), Opaque(brush.Color)),
            GradientBrush { GradientStops.Count: > 0 } brush =>
                (Opaque(brush.GradientStops[0].Color), Opaque(brush.GradientStops[^1].Color)),
            _ => (fallback, fallback),
        };
    }

    private void UpdateSelectionSizeBadge(Rect bounds)
    {
        if (!ShouldShowSelectionSize || bounds.Width <= 0 || bounds.Height <= 0)
        {
            SelectionSizeBadge.Visibility = Visibility.Collapsed;
            return;
        }
        if (_nativeSelectionFrame.IsSizeEnabled)
        {
            SelectionSizeBadge.Visibility = Visibility.Collapsed;
            if (PresentationSource.FromVisual(CaptureSurface) is not null)
                _nativeSelectionSize.SetAvoidRegions(GetVisibleOverlayScreenBounds());
            return;
        }

        var physicalBounds = GetPhysicalSelectionBoundsForBounds(bounds);
        if (physicalBounds.Width <= 0 || physicalBounds.Height <= 0)
        {
            SelectionSizeBadge.Visibility = Visibility.Collapsed;
            return;
        }
        SelectionSizeText.Text = $"{physicalBounds.Width} × {physicalBounds.Height}";
        SelectionSizeBadge.Visibility = Visibility.Visible;
        SelectionSizeBadge.InvalidateMeasure();
        SelectionSizeBadge.Measure(new WpfSize(double.PositiveInfinity, double.PositiveInfinity));

        var monitor = System.Windows.Forms.Screen.FromRectangle(new DrawingRectangle(
            physicalBounds.X, physicalBounds.Y, physicalBounds.Width, physicalBounds.Height)).Bounds;
        var screen = Rect.Intersect(new Rect(0, 0, CaptureSurface.ActualWidth, CaptureSurface.ActualHeight),
            new Rect(CaptureSurface.PointFromScreen(new WpfPoint(monitor.Left, monitor.Top)),
                CaptureSurface.PointFromScreen(new WpfPoint(monitor.Right, monitor.Bottom))));
        Span<Rect> obstacles = stackalloc Rect[3];
        var count = 0;
        AddObstacle(CaptureToolbar, obstacles, ref count);
        AddObstacle(InlineCustomColorPanel, obstacles, ref count);
        AddObstacle(ColorPickerPanel, obstacles, ref count);
        var placement = SelectionSizeBadgeLayout.Place(bounds, SelectionSizeBadge.DesiredSize,
            screen, obstacles[..count]);
        if (placement.IsEmpty)
        {
            SelectionSizeBadge.Visibility = Visibility.Collapsed;
            return;
        }
        Canvas.SetLeft(SelectionSizeBadge, placement.X);
        Canvas.SetTop(SelectionSizeBadge, placement.Y);
    }

    private void AddObstacle(FrameworkElement element, Span<Rect> obstacles, ref int count)
    {
        if (element.Visibility != Visibility.Visible || element.ActualWidth <= 0 || element.ActualHeight <= 0) return;
        obstacles[count++] = new Rect(element.TranslatePoint(new WpfPoint(0, 0), CaptureSurface),
            element.TranslatePoint(new WpfPoint(element.ActualWidth, element.ActualHeight), CaptureSurface));
    }
}
