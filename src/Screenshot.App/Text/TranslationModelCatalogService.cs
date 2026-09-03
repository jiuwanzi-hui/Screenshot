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

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, uri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
            request.Content = JsonContent.Create(new
            {
                model = model.Trim(),
                messages = new[] { new { role = "user", content = "Reply with OK." } },
                temperature = 0,
                max_tokens = 4,
            });
            using var response = await httpClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return TranslationModelTestResult.Failure("模型测试失败：API Key 无效或无权限。");
            }
            if (response.StatusCode == HttpStatusCode.PaymentRequired)
            {
                return TranslationModelTestResult.Failure("模型可访问，但账户余额或额度不足。");
            }
            if (!response.IsSuccessStatusCode)
            {
                var error = OpenAiCompatibleTranslationProvider.ExtractProviderError(body);
                return TranslationModelTestResult.Failure(
                    string.IsNullOrWhiteSpace(error)
                        ? $"模型测试失败（HTTP {(int)response.StatusCode}）。"
                        : $"模型测试失败：{error}");
            }

            using var document = JsonDocument.Parse(body);
            var hasChoice = document.RootElement.TryGetProperty("choices", out var choices) &&
                            choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0;
            return hasChoice
                ? new TranslationModelTestResult(true, $"模型可用：{model.Trim()}")
                : TranslationModelTestResult.Failure("接口返回成功，但没有返回 choices，无法确认模型可用。");
        }
        catch (OperationCanceledException)
        {
            return TranslationModelTestResult.Failure("模型测试已取消。");
        }
        catch (HttpRequestException)
        {
            return TranslationModelTestResult.Failure("无法连接模型接口，请检查地址和网络。");
        }
        catch (JsonException)
        {
            return TranslationModelTestResult.Failure("接口返回格式无法识别，不能确认模型可用。");
        }
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
