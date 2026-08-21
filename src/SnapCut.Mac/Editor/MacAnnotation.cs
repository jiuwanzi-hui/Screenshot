using Avalonia;
using Avalonia.Media;

namespace SnapCut.Mac.Editor;

internal enum MacEditorTool
{
    Rectangle,
    Ellipse,
    Arrow,
    Emoji,
    Number,
    Brush,
    Text,
    Mosaic,
}

internal enum MacArrowStyle
{
    Filled,
    Hollow,
}

internal abstract record MacAnnotation;

internal sealed record MacShapeAnnotation(
    MacEditorTool Tool,
    Point Start,
    Point End,
    Color Color,
    double StrokeWidth,
    MacArrowStyle ArrowStyle = MacArrowStyle.Filled) : MacAnnotation;

internal sealed record MacStrokeAnnotation(
    MacEditorTool Tool,
    IReadOnlyList<Point> Points,
    Color Color,
    double StrokeWidth) : MacAnnotation;

internal sealed record MacTextAnnotation(
    MacEditorTool Tool,
    Point Position,
    string Text,
    Color Color,
    double FontSize) : MacAnnotation;

internal sealed record MacNumberAnnotation(
    Point Position,
    int Number,
    Color Color,
    double Size) : MacAnnotation;

internal sealed record MacCaptureSelection(
    Rect Bounds,
    IReadOnlyList<MacAnnotation> Annotations,
    MacCaptureAction Action = MacCaptureAction.Complete);

internal enum MacCaptureAction
{
    Complete,
    Save,
    ScrollCapture,
    RecognizeText,
    CopyRecognizedText,
    PrivacyRedaction,
    Translation,
    PinImage,
    VideoRecording,
    QrRecognition,
    CaptureAllScreens,
}
