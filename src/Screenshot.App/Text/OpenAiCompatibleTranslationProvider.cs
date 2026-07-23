using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Screenshot.App.Text;

public sealed class OpenAiCompatibleTranslationProvider : ITranslationProvider
{
    private const string DefaultModel = "gpt-4.1-mini";
    private readonly string _endpoint;
    private readonly string _model;
    private readonly string? _apiKey;
    private readonly HttpClient _httpClient;

    public OpenAiCompatibleTranslationProvider(
        string endpoint,
        string model,
        string? apiKey,
        HttpClient httpClient)
    {
        _endpoint = NormalizeEndpoint(endpoint);
        var configuredModel = model?.Trim() ?? string.Empty;
        _model = configuredModel.Equals(DefaultModel, StringComparison.OrdinalIgnoreCase) &&
                  _endpoint.Contains("deepseek.com", StringComparison.OrdinalIgnoreCase)
            ? "deepseek-chat"
            : configuredModel;
        _apiKey = apiKey;
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    private static string NormalizeEndpoint(string? endpoint)
    {
        var value = endpoint?.Trim() ?? string.Empty;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return value;
        }

        var path = uri.AbsolutePath.TrimEnd('/');
        if (uri.Host.Equals("api.deepseek.com", StringComparison.OrdinalIgnoreCase))
        {
            path = path switch
            {
                "" => "/chat/completions",
                "/v1" => "/v1/chat/completions",
                _ => path,
            };
        }
        else if (uri.Host.Equals("api.openai.com", StringComparison.OrdinalIgnoreCase) &&
                 string.IsNullOrEmpty(path))
        {
            path = "/v1/chat/completions";
        }

        return new UriBuilder(uri) { Path = path }.Uri.AbsoluteUri;
    }

    public OpenAiCompatibleTranslationProvider(
        string endpoint,
        string? apiKey,
        HttpClient httpClient)
        : this(endpoint, DefaultModel, apiKey, httpClient)
    {
    }

    public string Id => TranslationProviderFactory.OpenAiCompatibleProviderId;

    public async Task<TranslationResult> TranslateAsync(
        string text,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return TranslationResult.Failure("没有可翻译的文字。");
        }

        if (string.IsNullOrWhiteSpace(_endpoint))
        {
            return TranslationResult.Failure("请配置翻译服务地址。");
        }

        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            return TranslationResult.Failure("请配置翻译服务密钥。");
        }

        if (string.IsNullOrWhiteSpace(_model))
        {
            return TranslationResult.Failure("请配置翻译模型，例如 deepseek-chat。");
        }

        if (!Uri.TryCreate(_endpoint, UriKind.Absolute, out var endpointUri) ||
            endpointUri.Scheme != Uri.UriSchemeHttps)
        {
            return TranslationResult.Failure("翻译服务地址必须是 HTTPS 地址。");
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, endpointUri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
            request.Content = JsonContent.Create(new
            {
                model = _model,
                temperature = 0,
                messages = new[]
                {
                    new
                    {
                        role = "system",
                        content = "Translate the supplied text. Return only the translation, without commentary or formatting.",
                    },
                    new
                    {
                        role = "user",
                        content = $"Source language: {sourceLanguage}\nTarget language: {targetLanguage}\n\n{text}",
                    },
                },
            });

            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return TranslationResult.Failure("翻译服务拒绝了凭据。");
            }

            if (!response.IsSuccessStatusCode)
            {
                return TranslationResult.Failure(
                    $"翻译服务请求失败（HTTP {(int)response.StatusCode}）。");
            }

            using var document = JsonDocument.Parse(
                await response.Content.ReadAsStreamAsync(cancellationToken));
            var translatedText = document.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString();

            return string.IsNullOrWhiteSpace(translatedText)
                ? TranslationResult.Failure("翻译服务未返回内容。")
                : new TranslationResult(true, translatedText.Trim(), ErrorMessage: null);
        }
        catch (OperationCanceledException)
        {
            return TranslationResult.Failure("翻译已取消。");
        }
        catch (HttpRequestException)
        {
            return TranslationResult.Failure("无法连接翻译服务。");
        }
        catch (JsonException)
        {
            return TranslationResult.Failure("翻译服务返回了无法识别的内容。");
        }
    }
}
