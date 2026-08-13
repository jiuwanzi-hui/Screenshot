using Screenshot.App.Core;
using WpfColor = System.Windows.Media.Color;
using WpfPoint = System.Windows.Point;
using WpfRect = System.Windows.Rect;

namespace Screenshot.App.Editor;

public abstract record EditorAnnotation;

public sealed record RectangleAnnotation(
    WpfRect Bounds,
    WpfColor StrokeColor,
    double StrokeWidth) : EditorAnnotation;

public sealed record EllipseAnnotation(
    WpfRect Bounds,
    WpfColor StrokeColor,
    double StrokeWidth) : EditorAnnotation;

public sealed record ArrowAnnotation(
    WpfPoint Start,
    WpfPoint End,
    WpfColor StrokeColor,
    double StrokeWidth,
    ArrowStyle Style = ArrowStyle.Filled) : EditorAnnotation;

public sealed record BrushAnnotation(
    IReadOnlyList<WpfPoint> Points,
    WpfColor StrokeColor,
    double StrokeWidth) : EditorAnnotation;

public sealed record TextAnnotation(
    WpfPoint Position,
    string Text,
    WpfColor Color,
    double FontSize) : EditorAnnotation;

public sealed record EmojiAnnotation(
    WpfPoint Position,
    string Sticker,
    double FontSize) : EditorAnnotation;

public sealed record NumberAnnotation(
    WpfPoint Position,
    double Size,
    WpfColor Color) : EditorAnnotation;

public sealed record TranslatedTextAnnotationRegion(
    WpfRect Bounds,
    string Text,
    double FontSize);

public sealed record TranslationOverlayAnnotation(
    IReadOnlyList<TranslatedTextAnnotationRegion> Regions) : EditorAnnotation;

public sealed record MosaicAnnotation(
    IReadOnlyList<WpfPoint> Points,
    double StrokeWidth,
    int BlockSize) : EditorAnnotation;
