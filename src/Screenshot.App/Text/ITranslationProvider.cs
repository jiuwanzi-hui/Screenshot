namespace Screenshot.App.Text;

public interface ITranslationProvider
{
    string Id { get; }

    Task<TranslationResult> TranslateAsync(
        string text,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken = default);
}
