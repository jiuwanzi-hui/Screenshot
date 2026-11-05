using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FontFamily = System.Windows.Media.FontFamily;

namespace Screenshot.App.Editor;

internal static class TextAnnotationLayout
{
    internal static readonly FontFamily Font = new("Microsoft YaHei UI");

    internal static Thickness Padding(double fontSize) => new(Math.Max(4, fontSize * .22), Math.Max(2, fontSize * .12),
        Math.Max(4, fontSize * .22), Math.Max(2, fontSize * .12));
    internal static double CornerRadius(double fontSize) => Math.Max(3, fontSize * .2);

    internal static TextBlock CreateText(string text, double size, System.Windows.Media.Color color) => new()
    {
        Text = text, FontFamily = Font, FontSize = size, Foreground = Brush(color), IsHitTestVisible = false,
    };

    internal static System.Windows.Size Measure(string text, double fontSize, bool background)
    {
        var block = CreateText(text, fontSize, Colors.Black);
        block.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
        var padding = background ? Padding(fontSize) : new Thickness(0);
        return new System.Windows.Size(Math.Ceiling(block.DesiredSize.Width + padding.Left + padding.Right + (background ? 2 : 0)),
            Math.Ceiling(block.DesiredSize.Height + padding.Top + padding.Bottom + (background ? 2 : 0)));
    }

    internal static FrameworkElement CreateVisual(TextAnnotation annotation)
    {
        var text = CreateText(annotation.Text, annotation.FontSize, annotation.Color);
        if (annotation.Background is not { } background) return text;
        return new Border
        {
            Child = text, Background = Brush(background.Fill), BorderBrush = Brush(background.Border),
            BorderThickness = new Thickness(1), Padding = Padding(annotation.FontSize),
            CornerRadius = new CornerRadius(CornerRadius(annotation.FontSize)), IsHitTestVisible = false,
        };
    }

    internal static SolidColorBrush Brush(System.Windows.Media.Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
