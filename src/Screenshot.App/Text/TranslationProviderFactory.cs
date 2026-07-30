using System.Net.Http;
using Screenshot.App.Core;

namespace Screenshot.App.Text;

public static class TranslationProviderFactory
{
    public const string OpenAiCompatibleProviderId = "OpenAICompatible";
    public const string OfflineProviderId = "OfflineBergamot";

    // The OpenAI-compatible provider is the only supported type. Always resolve
    // to its stable id so legacy free-text values cannot silently disable translation.
    public static string ResolveProviderId(string? configuredProvider)
    {
        return OpenAiCompatibleProviderId;
    }

    public static string NormalizeModel(string? endpoint, string? configuredModel)
    {
        var model = configuredModel?.Trim() ?? string.Empty;
        if (!(endpoint?.Contains(
                "deepseek.com",
                StringComparison.OrdinalIgnoreCase) ?? false))
        {
            return model;
        }

        return model.ToLowerInvariant() switch
        {
            "" or "deepseek" or "deepseek chat" or "deepseek-chat" or
                "deepseek reasoner" or "deepseek-reasoner" or
                "gpt-4.1-mini" => "deepseek-v4-flash",
            _ => model,
        };
    }

    public static ITranslationProvider Create(
        AppSettings settings,
        ITranslationCredentialStore credentialStore,
        HttpClient httpClient,
        OfflineTranslationModelManager? offlineModelManager = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(credentialStore);
        ArgumentNullException.ThrowIfNull(httpClient);

        ITranslationProvider CreateProvider(TranslationProviderKind provider)
        {
            if (provider == TranslationProviderKind.Offline)
            {
                return new OfflineTranslationProvider(
                    offlineModelManager ?? OfflineTranslationModelManager.Shared);
            }

            var providerId = ResolveProviderId(settings.TranslationProvider);
            return new OpenAiCompatibleTranslationProvider(
                settings.TranslationEndpoint,
                settings.TranslationModel,
                credentialStore.GetApiKey(providerId),
                httpClient);
        }

        return new OrderedTranslationProvider(
            settings.ResolveTranslationProviderPriority()
                .Select(CreateProvider)
                .ToArray());
    }
}
