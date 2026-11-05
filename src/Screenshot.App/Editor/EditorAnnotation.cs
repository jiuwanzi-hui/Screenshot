using Screenshot.App.Core;
using WpfColor = System.Windows.Media.Color;
using WpfPoint = System.Windows.Point;
using WpfRect = System.Windows.Rect;

namespace Screenshot.App.Editor;

public abstract record EditorAnnotation;

public sealed record LineAnnotation(
    WpfPoint Start,
    WpfPoint End,
    WpfColor StrokeColor,
    double StrokeWidth,
    bool IsWavy = false) : EditorAnnotation;

public sealed record RectangleMosaicAnnotation(
    WpfRect Bounds,
    int BlockSize) : EditorAnnotation;

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
    ArrowStyle Style = ArrowStyle.Filled,
    bool IsDoubleArrow = false) : EditorAnnotation;

public sealed record CurvedArrowAnnotation(
    IReadOnlyList<WpfPoint> Points,
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
    double FontSize) : EditorAnnotation
{
    public TextAnnotationBackground? Background { get; init; }
}

// Capture the chosen theme colors so exports and subsequent edits retain the
// same appearance even after the application theme changes.
public sealed record TextAnnotationBackground(WpfColor Fill, WpfColor Border);

public sealed record EmojiAnnotation(
    WpfPoint Position,
    string Sticker,
    double FontSize) : EditorAnnotation;

public sealed record NumberAnnotation(
    WpfPoint Position,
    double Size,
    WpfColor Color,
    int? Value = null,
    NumberingStyle Style = NumberingStyle.Digits) : EditorAnnotation;

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
