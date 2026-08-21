namespace Screenshot.App.Text;

public sealed class NoTranslationProvider : ITranslationProvider
{
    public const string ProviderId = "None";

    public string Id => ProviderId;

    public Task<TranslationResult> TranslateAsync(
        string text,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(
            TranslationResult.Failure("尚未配置翻译服务。"));
    }

    public Task<TranslationSegmentsResult> TranslateSegmentsAsync(
        IReadOnlyList<string> segments,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(
            TranslationSegmentsResult.Failure("尚未配置翻译服务。"));
    }
}
