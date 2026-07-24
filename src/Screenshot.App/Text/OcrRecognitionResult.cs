namespace Screenshot.App.Text;

public sealed record OcrTextRegion(
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

    public static OcrRecognitionResult Failure(string errorMessage)
    {
        return new OcrRecognitionResult(false, string.Empty, errorMessage);
    }
}
