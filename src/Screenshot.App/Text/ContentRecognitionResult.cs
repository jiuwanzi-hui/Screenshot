namespace Screenshot.App.Text;

public sealed record ContentRecognitionResult(
    bool IsSuccess,
    string Title,
    string Content,
    string? ErrorMessage = null)
{
    public RecognizedContentRegion? Region { get; init; }

    /// <summary>Optional rich clipboard representation for structured results.</summary>
    public string? ClipboardHtml { get; init; }

    public static ContentRecognitionResult Failure(
        string title,
        string errorMessage)
    {
        return new ContentRecognitionResult(
            false,
            title,
            string.Empty,
            errorMessage);
    }
}

public sealed record RecognizedContentRegion(
    double X,
    double Y,
    double Width,
    double Height)
{
    public double CenterX => X + (Width / 2);

    public double CenterY => Y + (Height / 2);
}
