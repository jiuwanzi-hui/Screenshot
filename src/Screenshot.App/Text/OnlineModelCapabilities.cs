using Screenshot.App.Core;

namespace Screenshot.App.Text;

internal static class OnlineModelCapabilities
{
    internal static OnlineModelPurpose Resolve(AiTranslationProfile profile)
    {
        if (profile.Purpose != OnlineModelPurpose.Automatic) return profile.Purpose;
        var model = profile.Model.Trim();
        return model.StartsWith("gpt-image-", StringComparison.OrdinalIgnoreCase) ||
            model.StartsWith("images-", StringComparison.OrdinalIgnoreCase) ||
            model.StartsWith("dall-e-", StringComparison.OrdinalIgnoreCase)
            ? OnlineModelPurpose.ImageGeneration : OnlineModelPurpose.TextTranslation;
    }
    internal static bool CanTranslate(AiTranslationProfile profile) =>
        TranslationProviderFactory.IsPublicProvider(profile.Provider) || Resolve(profile) == OnlineModelPurpose.TextTranslation;
}
