namespace Screenshot.App.Text;

public sealed record TranslationResult(
    bool IsSuccess,
    string Text,
    string? ErrorMessage)
{
    public static TranslationResult Failure(string errorMessage)
    {
        return new TranslationResult(false, string.Empty, errorMessage);
    }
}
