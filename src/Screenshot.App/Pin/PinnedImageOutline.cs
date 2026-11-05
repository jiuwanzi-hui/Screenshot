using System.Windows;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;

namespace Screenshot.App.Pin;

/// <summary>Display-only, pixel-aligned edges, independent of image resampling.</summary>
public sealed class PinnedImageOutline : FrameworkElement
{
    public static readonly DependencyProperty EdgeBrushProperty = DependencyProperty.Register(
        nameof(EdgeBrush), typeof(Brush), typeof(PinnedImageOutline),
        new FrameworkPropertyMetadata(Brushes.SlateGray, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush EdgeBrush
    {
        get => (Brush)GetValue(EdgeBrushProperty);
        set => SetValue(EdgeBrushProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var x = 1 / dpi.DpiScaleX;
        var y = 1 / dpi.DpiScaleY;
        var width = Math.Floor(ActualWidth / x) * x;
        var height = Math.Floor(ActualHeight / y) * y;
        DrawRing(drawingContext, EdgeBrush, new Rect(0, 0, width, height), x, y);
        if (width > x * 4 && height > y * 4)
        {
            drawingContext.PushOpacity(.75);
            DrawRing(drawingContext, Brushes.White, new Rect(x, y, width - 2 * x, height - 2 * y), x, y);
            drawingContext.Pop();
        }
    }

    private static void DrawRing(DrawingContext context, Brush brush, Rect rect, double x, double y)
    {
        if (rect.Width <= 0 || rect.Height <= 0) return;
        x = Math.Min(x, rect.Width);
        y = Math.Min(y, rect.Height / 2);
        // Filled bands have no diagonal pen joins at fractional window sizes.
        context.DrawRectangle(brush, null, new Rect(rect.Left, rect.Top, rect.Width, y));
        context.DrawRectangle(brush, null, new Rect(rect.Left, rect.Bottom - y, rect.Width, y));
        context.DrawRectangle(brush, null, new Rect(rect.Left, rect.Top + y, x, rect.Height - 2 * y));
        context.DrawRectangle(brush, null, new Rect(rect.Right - x, rect.Top + y, x, rect.Height - 2 * y));
    }
}
