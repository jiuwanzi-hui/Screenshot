namespace Screenshot.App.Text;

public sealed record OcrRecognitionResult(
    bool IsSuccess,
    string Text,
    string? ErrorMessage)
{
    public static OcrRecognitionResult Failure(string errorMessage)
    {
        return new OcrRecognitionResult(false, string.Empty, errorMessage);
    }
}
