namespace Screenshot.App.Text;

public sealed class OrderedTranslationProvider : ITranslationProvider
{
    private static readonly TimeSpan DefaultOfflineTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan DefaultOnlineTimeout = TimeSpan.FromSeconds(35);
    private static readonly TimeSpan DefaultLargeModelTimeout = TimeSpan.FromMinutes(3);
    private readonly IReadOnlyList<ITranslationProvider> _providers;
    private readonly TimeSpan _offlineTimeout;
    private readonly TimeSpan _onlineTimeout;

    public OrderedTranslationProvider(IReadOnlyList<ITranslationProvider> providers)
        : this(providers, DefaultOfflineTimeout, DefaultOnlineTimeout)
    {
    }

    internal OrderedTranslationProvider(
        IReadOnlyList<ITranslationProvider> providers,
        TimeSpan offlineTimeout,
        TimeSpan onlineTimeout)
    {
        ArgumentNullException.ThrowIfNull(providers);
        if (providers.Count == 0)
        {
            throw new ArgumentException("至少需要一个翻译提供方。", nameof(providers));
        }

        _providers = providers.ToArray();
        _offlineTimeout = offlineTimeout > TimeSpan.Zero
            ? offlineTimeout
            : throw new ArgumentOutOfRangeException(nameof(offlineTimeout));
        _onlineTimeout = onlineTimeout > TimeSpan.Zero
            ? onlineTimeout
            : throw new ArgumentOutOfRangeException(nameof(onlineTimeout));
    }

    public string Id => "OrderedFallback";

    public IReadOnlyList<string> ProviderIds =>
        _providers.Select(provider => provider.Id).ToArray();

    public async Task<TranslationResult> TranslateAsync(
        string text,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken = default)
    {
        var errors = new List<string>(_providers.Count);
        foreach (var provider in _providers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var result = await InvokeWithTimeoutAsync(
                    provider,
                    text,
                    sourceLanguage,
                    targetLanguage,
                    cancellationToken);
                if (result.IsSuccess && !ContainsUntranslatedHanText(
                        [text],
                        [result.Text],
                        targetLanguage))
                {
                    return result;
                }

                errors.Add(FormatError(
                    provider,
                    result.IsSuccess
                        ? "译文仍包含未翻译的中文"
                        : result.ErrorMessage));
            }
            catch (OperationCanceledException) when (
                cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                errors.Add(FormatError(provider, exception.Message));
            }
        }

        return TranslationResult.Failure(CreateFailureMessage(errors));
    }

    public async Task<TranslationSegmentsResult> TranslateSegmentsAsync(
        IReadOnlyList<string> segments,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken = default)
    {
        var errors = new List<string>(_providers.Count);
        foreach (var provider in _providers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var result = await InvokeSegmentsWithTimeoutAsync(
                    provider,
                    segments,
                    sourceLanguage,
                    targetLanguage,
                    cancellationToken);
                if (result.IsSuccess && !ContainsUntranslatedHanText(
                        segments,
                        result.Segments,
                        targetLanguage))
                {
                    return result;
                }

                errors.Add(FormatError(
                    provider,
                    result.IsSuccess
                        ? "译文仍包含未翻译的中文"
                        : result.ErrorMessage));
            }
            catch (OperationCanceledException) when (
                cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                errors.Add(FormatError(provider, exception.Message));
            }
        }

        return TranslationSegmentsResult.Failure(CreateFailureMessage(errors));
    }

    private static string FormatError(
        ITranslationProvider provider,
        string? errorMessage)
    {
        var label = provider.Id switch
        {
            TranslationProviderFactory.OpenAiCompatibleProviderId => "在线大模型",
            TranslationProviderFactory.OfflineProviderId => "离线模型",
            TranslationProviderFactory.LocalLargeModelProviderId =>
                "Qwen 本机大模型",
            _ => provider.Id,
        };
        return $"{label}：{(string.IsNullOrWhiteSpace(errorMessage) ? "不可用" : errorMessage)}";
    }

    private static string CreateFailureMessage(IReadOnlyList<string> errors)
    {
        return "所有翻译方式均不可用。" + string.Join("；", errors);
    }

    private async Task<TranslationResult> InvokeWithTimeoutAsync(
        ITranslationProvider provider,
        string text,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken)
    {
        using var providerCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        var providerTask = Task.Run(
            () => provider.TranslateAsync(
                text,
                sourceLanguage,
                targetLanguage,
                providerCancellation.Token),
            CancellationToken.None);
        try
        {
            return await providerTask.WaitAsync(
                    GetTimeout(provider),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            providerCancellation.Cancel();
            return TranslationResult.Failure("翻译超时，已切换到下一种翻译方式");
        }
    }

    private async Task<TranslationSegmentsResult> InvokeSegmentsWithTimeoutAsync(
        ITranslationProvider provider,
        IReadOnlyList<string> segments,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken)
    {
        using var providerCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        var providerTask = Task.Run(
            () => provider.TranslateSegmentsAsync(
                segments,
                sourceLanguage,
                targetLanguage,
                providerCancellation.Token),
            CancellationToken.None);
        try
        {
            return await providerTask.WaitAsync(
                    GetTimeout(provider),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            providerCancellation.Cancel();
            return TranslationSegmentsResult.Failure(
                "翻译超时，已切换到下一种翻译方式");
        }
    }

    private TimeSpan GetTimeout(ITranslationProvider provider)
    {
        if (string.Equals(
                provider.Id,
                TranslationProviderFactory.LocalLargeModelProviderId,
                StringComparison.OrdinalIgnoreCase))
        {
            return DefaultLargeModelTimeout;
        }

        return string.Equals(
            provider.Id,
            TranslationProviderFactory.OfflineProviderId,
            StringComparison.OrdinalIgnoreCase)
            ? _offlineTimeout
            : _onlineTimeout;
    }

    internal static bool ContainsUntranslatedHanText(
        IReadOnlyList<string> sourceSegments,
        IReadOnlyList<string> translatedSegments,
        string targetLanguage)
    {
        if (!string.Equals(
                TranslationLanguageCatalog.NormalizeOfflineCode(targetLanguage),
                "en",
                StringComparison.OrdinalIgnoreCase) ||
            !sourceSegments.Any(segment => segment.Count(IsHanCharacter) >= 2))
        {
            return false;
        }

        return translatedSegments.Any(segment => segment.Count(IsHanCharacter) >= 2);
    }

    private static bool IsHanCharacter(char value)
    {
        return value is >= '\u3400' and <= '\u4DBF' or
            >= '\u4E00' and <= '\u9FFF' or
            >= '\uF900' and <= '\uFAFF';
    }
}
