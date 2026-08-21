using Avalonia;

namespace SnapCut.Mac.Text;

internal sealed record MacOcrTextRegion(
    string Text,
    Rect Bounds,
    double EstimatedFontSize);

internal sealed record MacOcrWordRegion(string Text, Rect Bounds);

internal sealed record MacOcrRecognitionResult(
    bool IsSuccess,
    string Text,
    string? ErrorMessage)
{
    public IReadOnlyList<MacOcrTextRegion> Regions { get; init; } = [];

    public IReadOnlyList<MacOcrWordRegion> Words { get; init; } = [];

    public static MacOcrRecognitionResult Failure(string errorMessage) =>
        new(false, string.Empty, errorMessage);
}
