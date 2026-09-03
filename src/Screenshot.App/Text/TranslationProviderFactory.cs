using System.Net.Http;
using Screenshot.App.Core;

namespace Screenshot.App.Text;

public static class TranslationProviderFactory
{
    /// <summary>
    /// Stable id retained for existing settings. It now represents the first
    /// "custom compatible endpoint" entry in the provider picker.
    /// </summary>
    public const string OpenAiCompatibleProviderId = "OpenAICompatible";
    public const string OfflineProviderId = "OfflineBergamot";
    public const string LocalLargeModelProviderId = "OfflineQwenLargeModel";

    public sealed record ProviderDefinition(
        string Id,
        string DisplayName,
        string OfficialEndpoint,
        string OfficialSite,
        string DefaultModel);

    public static IReadOnlyList<ProviderDefinition> ProviderDefinitions { get; } =
    [
        new(
            OpenAiCompatibleProviderId,
            "自定义兼容接口",
            string.Empty,
            "可填写任何 OpenAI Chat Completions 兼容服务",
            string.Empty),
        new(
            "OpenAI",
            "OpenAI",
            "https://api.openai.com/v1",
            "官方接口：https://platform.openai.com/docs/api-reference",
            "gpt-4.1-mini"),
        new(
            "DeepSeek",
            "DeepSeek",
            "https://api.deepseek.com",
            "官方接口：https://api-docs.deepseek.com/",
            "deepseek-v4-flash"),
        new(
            "DashScope",
            "通义千问（阿里云百炼）",
            "https://dashscope.aliyuncs.com/compatible-mode/v1",
            "官方接口：https://help.aliyun.com/zh/model-studio/",
            "qwen-plus"),
        new(
            "Zhipu",
            "智谱 GLM",
            "https://open.bigmodel.cn/api/paas/v4",
            "官方接口：https://open.bigmodel.cn/dev/api",
            "glm-4-flash"),
        new(
            "Moonshot",
            "月之暗面 Kimi",
            "https://api.moonshot.cn/v1",
            "官方接口：https://platform.moonshot.cn/docs/intro",
            "moonshot-v1-8k"),
        new(
            "SiliconFlow",
            "硅基流动",
            "https://api.siliconflow.cn/v1",
            "官方接口：https://docs.siliconflow.cn/",
            "Qwen/Qwen2.5-7B-Instruct"),
        new(
            "Groq",
            "Groq",
            "https://api.groq.com/openai/v1",
            "官方接口：https://console.groq.com/docs",
            "llama-3.3-70b-versatile"),
        new(
            "Together",
            "Together AI",
            "https://api.together.xyz/v1",
            "官方接口：https://docs.together.ai/reference",
            "meta-llama/Llama-3.3-70B-Instruct-Turbo"),
        new(
            "VolcengineArk",
            "火山方舟（豆包）",
            "https://ark.cn-beijing.volces.com/api/v3",
            "官方接口：https://www.volcengine.com/docs/82379/1298454",
            "doubao-1-5-pro-32k-250115"),
        new(
            "TencentHunyuan",
            "腾讯混元",
            "https://api.hunyuan.cloud.tencent.com/v1",
            "官方接口：https://cloud.tencent.com/document/product/1729",
            "hunyuan-turbo"),
        new(
            "Yi",
            "零一万物 Yi",
            "https://api.lingyiwanwu.com/v1",
            "官方接口：https://platform.lingyiwanwu.com/docs",
            "yi-lightning"),
        new(
            "AnthropicClaude",
            "Anthropic Claude（兼容接口）",
            "https://api.anthropic.com/v1",
            "兼容接口说明：https://docs.anthropic.com/en/api/openai-sdk",
            "claude-sonnet-4-0"),
        new(
            "GoogleGemini",
            "Google Gemini（兼容接口）",
            "https://generativelanguage.googleapis.com/v1beta/openai",
            "兼容接口说明：https://ai.google.dev/gemini-api/docs/openai",
            "gemini-2.5-flash"),
        new(
            "XaiGrok",
            "xAI Grok",
            "https://api.x.ai/v1",
            "官方接口：https://docs.x.ai/docs",
            "grok-3-mini"),
    ];

    public static string ResolveProviderId(string? configuredProvider)
    {
        var value = configuredProvider?.Trim();
        return ProviderDefinitions.Any(provider =>
                provider.Id.Equals(value, StringComparison.OrdinalIgnoreCase))
            ? ProviderDefinitions.First(provider =>
                provider.Id.Equals(value, StringComparison.OrdinalIgnoreCase)).Id
            : OpenAiCompatibleProviderId;
    }

    public static ProviderDefinition GetDefinition(string? providerId)
    {
        var resolvedId = ResolveProviderId(providerId);
        return ProviderDefinitions.First(provider =>
            provider.Id.Equals(resolvedId, StringComparison.OrdinalIgnoreCase));
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
        OfflineTranslationModelManager? offlineModelManager = null,
        LocalLargeTranslationModelManager? localLargeModelManager = null,
        bool preferFastOffline = false)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(credentialStore);
        ArgumentNullException.ThrowIfNull(httpClient);

        IEnumerable<ITranslationProvider> CreateProviders(
            TranslationProviderKind provider)
        {
            if (provider == TranslationProviderKind.Offline)
            {
                var bergamot = new OfflineTranslationProvider(
                    offlineModelManager ??
                    OfflineTranslationModelManager.Shared,
                    preferFastOffline
                        ? OfflineTranslationQuality.Fast
                        : settings.OfflineTranslationQuality);
                if (settings.OfflineTranslationEngine ==
                    OfflineTranslationEngine.QwenLargeModel)
                {
                    // Bergamot is a purpose-built translation engine and is
                    // both faster and more deterministic for installed
                    // language pairs. Qwen remains available when a pair is
                    // missing or Bergamot cannot translate the input.
                    return
                    [
                        bergamot,
                        new LocalLargeModelTranslationProvider(
                            localLargeModelManager ??
                            LocalLargeTranslationModelManager.Shared),
                    ];
                }

                return [bergamot];
            }

            var profiles = (settings.TranslationProfiles ?? [])
                .Where(profile => profile.IsEnabled)
                .ToArray();
            if (profiles.Length == 0)
            {
                var providerId = ResolveProviderId(settings.TranslationProvider);
                return
                [
                    new OpenAiCompatibleTranslationProvider(
                        settings.TranslationEndpoint,
                        settings.TranslationModel,
                        credentialStore.GetApiKey(providerId),
                        httpClient),
                ];
            }

            return profiles
                .Select(profile =>
                {
                    var providerId = ResolveProviderId(profile.Provider);
                    return (ITranslationProvider)new OpenAiCompatibleTranslationProvider(
                        profile.Endpoint,
                        profile.Model,
                        credentialStore.GetApiKey(profile.Id, providerId),
                        httpClient);
                })
                .ToArray();
        }

        return new OrderedTranslationProvider(
            settings.ResolveTranslationProviderPriority()
                .SelectMany(CreateProviders)
                .ToArray());
    }
}
