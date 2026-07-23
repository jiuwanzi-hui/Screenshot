namespace Screenshot.App.Capture;

public sealed record ScrollCaptureResult(
    bool IsSuccess,
    CapturedImage? Image,
    string? ErrorMessage)
{
    public static ScrollCaptureResult Failure(string errorMessage)
    {
        return new ScrollCaptureResult(false, Image: null, errorMessage);
    }
}
