using WpfColor = System.Windows.Media.Color;
using WpfPoint = System.Windows.Point;
using WpfRect = System.Windows.Rect;

namespace Screenshot.App.Editor;

public abstract record EditorAnnotation;

public sealed record RectangleAnnotation(
    WpfRect Bounds,
    WpfColor StrokeColor,
    double StrokeWidth) : EditorAnnotation;

public sealed record ArrowAnnotation(
    WpfPoint Start,
    WpfPoint End,
    WpfColor StrokeColor,
    double StrokeWidth) : EditorAnnotation;

public sealed record BrushAnnotation(
    IReadOnlyList<WpfPoint> Points,
    WpfColor StrokeColor,
    double StrokeWidth) : EditorAnnotation;

public sealed record TextAnnotation(
    WpfPoint Position,
    string Text,
    WpfColor Color,
    double FontSize) : EditorAnnotation;

public sealed record MosaicAnnotation(
    IReadOnlyList<WpfPoint> Points,
    double StrokeWidth,
    int BlockSize) : EditorAnnotation;
