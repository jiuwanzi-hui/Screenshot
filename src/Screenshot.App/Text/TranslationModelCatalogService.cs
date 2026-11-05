using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Screenshot.App.Text;

public sealed record TranslationModelCatalogResult(
    bool IsSuccess,
    IReadOnlyList<string> Models,
    string? ErrorMessage)
{
    public static TranslationModelCatalogResult Failure(string errorMessage)
    {
        return new TranslationModelCatalogResult(false, [], errorMessage);
    }
}

public sealed record TranslationModelTestResult(
    bool IsSuccess,
    string Message)
{
    public static TranslationModelTestResult Failure(string message) =>
        new(false, message);
}

public static class TranslationModelCatalogService
{
    internal static async Task<TranslationModelTestResult> TestAsync(Core.AiTranslationProfile profile,
        string? apiKey, HttpClient client, CancellationToken cancellationToken = default)
    {
        if (!TranslationProviderFactory.IsPublicProvider(profile.Provider) &&
            OnlineModelCapabilities.Resolve(profile) != Core.OnlineModelPurpose.TextTranslation)
        {
            return await Task.Run(async () =>
            {
                if (OnlineModelCapabilities.Resolve(profile) == Core.OnlineModelPurpose.ImageGeneration)
                {
                    var result = await ImageModelService.ProcessAsync(profile, apiKey, client, [], [],
                        "Create one simple image of a blue circle on a white background.", cancellationToken, updateProfileHealth: false).ConfigureAwait(false);
                    return new TranslationModelTestResult(result.IsSuccess && result.Images.Count > 0,
                        result.IsSuccess ? $"图片生成测试成功：{profile.Model}，已验证返回图片；编辑能力请在问图中附图验证。" : result.Error ?? "图片测试未完成。");
                }
                var pixels = Enumerable.Repeat((byte)0, 32 * 32 * 4).ToArray();
                for (var i = 0; i < pixels.Length; i += 4) { pixels[i + 2] = 255; pixels[i + 3] = 255; }
                var image = System.Windows.Media.Imaging.BitmapSource.Create(32, 32, 96, 96,
                    System.Windows.Media.PixelFormats.Bgra32, null, pixels, 128); image.Freeze();
                using var credentials = new TestImageCredential(apiKey);
                var reply = await new ImageQuestionService(client, credentials).AskWithImagesAsync(profile, [ImageQuestionService.EncodeImage(image)], [],
                    "What is the main color of this image? Answer with one color name in English.", cancellationToken, updateProfileHealth: false).ConfigureAwait(false);
                var valid = reply.IsSuccess && (reply.Text.Contains("red", StringComparison.OrdinalIgnoreCase) || reply.Text.Contains('红'));
                return new TranslationModelTestResult(valid, valid ? $"图片识别测试成功：{profile.Model}。" : reply.Error ?? "接口有响应，但未正确识别测试图片；请检查模型是否支持图片输入。");
            }, cancellationToken);
        }
        if (!TranslationProviderFactory.IsPublicProvider(profile.Provider))
            return await TestAsync(profile.Endpoint, profile.Model, apiKey, client, cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var result = await TranslationProviderFactory.CreateOnlineProvider(profile, apiKey, client).TranslateSegmentsAsync(
                ["The meeting starts at 9:30.", "Please save this document."], "en", "zh-Hans", timeout.Token);
            return result.IsSuccess && result.FailedSegmentIndices is not { Count: > 0 } && result.Segments.Count == 2
                ? new(true, $"公共翻译测试成功：{watch.Elapsed.TotalSeconds:F2} 秒，两段译文均已校验。")
                : TranslationModelTestResult.Failure(result.ErrorMessage ?? "公共翻译未通过完整性校验。");
        }
        catch (OperationCanceledException)
        {
            return TranslationModelTestResult.Failure(cancellationToken.IsCancellationRequested ? "翻译测试已取消。" : "公共翻译测试超时。");
        }
    }

    public static async Task<TranslationModelTestResult> TestAsync(
        string endpoint,
        string model,
        string? apiKey,
        HttpClient httpClient,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        var chatEndpoint = OpenAiCompatibleEndpointResolver
            .NormalizeChatCompletionsEndpoint(endpoint);
        if (!Uri.TryCreate(chatEndpoint, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps)
        {
            return TranslationModelTestResult.Failure("请配置有效的 HTTPS 服务地址。");
        }

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return TranslationModelTestResult.Failure("请先填写 API Key。");
        }

        if (string.IsNullOrWhiteSpace(model))
        {
            return TranslationModelTestResult.Failure("请填写模型名称，例如 glm-4-flashx。");
        }

        // Use the same request and validation as screenshot translation. A
        // choices array (even an empty/reasoning-only answer) is not proof that
        // the selected model can translate complete segments.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            var provider = new OpenAiCompatibleTranslationProvider(endpoint, model, apiKey, httpClient);
            var result = await provider.TranslateSegmentsAsync(
                ["The meeting starts at 9:30.", "Please save this document."], "en", "zh-Hans", timeout.Token);
            return result.IsSuccess && result.FailedSegmentIndices is not { Count: > 0 } &&
                   result.Segments.Count == 2
                ? new TranslationModelTestResult(true, $"翻译测试成功：{model.Trim()}，两段译文均已校验。")
                : TranslationModelTestResult.Failure(result.ErrorMessage ?? "接口已连接，但分段翻译未通过校验。");
        }
        catch (OperationCanceledException)
        {
            return TranslationModelTestResult.Failure(cancellationToken.IsCancellationRequested
                ? "模型测试已取消。" : "翻译测试超过 60 秒，请检查服务响应速度。");
        }
    }

    private sealed class TestImageCredential(string? key) : ITranslationCredentialStore, IDisposable
    {
        private string? _key = key;
        public string? GetApiKey(string id) => _key;
        public void SetApiKey(string id, string? value) => throw new NotSupportedException();
        public void Dispose() => _key = null;
    }

    public static async Task<TranslationModelCatalogResult> FetchAsync(
        string endpoint,
        string? apiKey,
        HttpClient httpClient,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        var modelsEndpoint =
            OpenAiCompatibleEndpointResolver.CreateModelsEndpoint(endpoint);
        if (modelsEndpoint is null)
        {
            return TranslationModelCatalogResult.Failure(
                "请配置有效的 HTTPS 服务地址。");
        }

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return TranslationModelCatalogResult.Failure(
                "请先填写并保存 API Key。");
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, modelsEndpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Bearer",
                apiKey.Trim());
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(
                "application/json"));
            using var response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            var responseBody = await response.Content.ReadAsStringAsync(
                cancellationToken);

            if (response.StatusCode is HttpStatusCode.Unauthorized or
                HttpStatusCode.Forbidden)
            {
                return TranslationModelCatalogResult.Failure(
                    "模型列表接口拒绝了 API Key；部分兼容服务不开放模型列表，请手动输入模型后点击“测试模型”。");
            }

            if (!response.IsSuccessStatusCode)
            {
                var providerError =
                    OpenAiCompatibleTranslationProvider.ExtractProviderError(
                        responseBody);
                return TranslationModelCatalogResult.Failure(
                    string.IsNullOrWhiteSpace(providerError)
                        ? $"获取模型失败（HTTP {(int)response.StatusCode}）。"
                        : $"获取模型失败（HTTP {(int)response.StatusCode}）：{providerError}");
            }

            var models = ParseModels(responseBody);
            return models.Length == 0
                ? TranslationModelCatalogResult.Failure(
                    "接口未返回可用模型；你仍可以手动输入模型标识。")
                : new TranslationModelCatalogResult(
                    true,
                    models,
                    ErrorMessage: null);
        }
        catch (OperationCanceledException)
        {
            return TranslationModelCatalogResult.Failure("获取模型已取消。");
        }
        catch (HttpRequestException)
        {
            return TranslationModelCatalogResult.Failure(
                "无法连接模型列表接口。");
        }
        catch (JsonException)
        {
            return TranslationModelCatalogResult.Failure(
                "模型列表接口返回了无法识别的内容。");
        }
    }

    private static string[] ParseModels(string responseBody)
    {
        using var document = JsonDocument.Parse(responseBody);
        if (!TryFindModelItems(document.RootElement, out var items))
        {
            return [];
        }

        return items.EnumerateArray()
            .Select(ReadModelId)
            .Where(model => !string.IsNullOrWhiteSpace(model))
            .Select(model => model!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(model => model, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool TryFindModelItems(JsonElement value, out JsonElement items)
    {
        if (value.ValueKind == JsonValueKind.Array)
        {
            items = value;
            return true;
        }

        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var propertyName in new[]
                     {
                         "data", "models", "items", "model_list", "modelList", "result",
                     })
            {
                if (value.TryGetProperty(propertyName, out var nested) &&
                    TryFindModelItems(nested, out items))
                {
                    return true;
                }
            }
        }

        items = default;
        return false;
    }

    private static string? ReadModelId(JsonElement item)
    {
        if (item.ValueKind == JsonValueKind.String)
        {
            return item.GetString()?.Trim();
        }

        if (item.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var propertyName in new[]
                 { "id", "name", "model", "model_id", "modelId" })
        {
            if (item.TryGetProperty(propertyName, out var value) &&
                value.ValueKind == JsonValueKind.String)
            {
                return value.GetString()?.Trim();
            }
        }

        return null;
    }
}
