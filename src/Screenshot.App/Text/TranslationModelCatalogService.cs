using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
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

public static class TranslationModelCatalogService
{
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
                    "模型列表接口拒绝了 API Key。");
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
        var root = document.RootElement;
        JsonElement items;
        if (root.ValueKind == JsonValueKind.Array)
        {
            items = root;
        }
        else if (root.ValueKind == JsonValueKind.Object &&
                 root.TryGetProperty("data", out var data) &&
                 data.ValueKind == JsonValueKind.Array)
        {
            items = data;
        }
        else if (root.ValueKind == JsonValueKind.Object &&
                 root.TryGetProperty("models", out var models) &&
                 models.ValueKind == JsonValueKind.Array)
        {
            items = models;
        }
        else
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

        foreach (var propertyName in new[] { "id", "name", "model" })
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
