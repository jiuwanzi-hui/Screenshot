namespace Screenshot.App.Text;

public sealed record OcrTextRegion(
    string Text,
    double X,
    double Y,
    double Width,
    double Height)
{
    public double EstimatedFontSize { get; init; }
}

public sealed record OcrWordRegion(
    string Text,
    double X,
    double Y,
    double Width,
    double Height);

public sealed record OcrRecognitionResult(
    bool IsSuccess,
    string Text,
    string? ErrorMessage)
{
    public IReadOnlyList<OcrTextRegion> Regions { get; init; } = [];

    public IReadOnlyList<OcrWordRegion> Words { get; init; } = [];

    public static OcrRecognitionResult Failure(string errorMessage)
    {
        return new OcrRecognitionResult(false, string.Empty, errorMessage);
    }
}
