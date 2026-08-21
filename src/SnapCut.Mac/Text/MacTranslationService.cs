using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using SnapCut.Mac.App;

namespace SnapCut.Mac.Text;

internal sealed record MacTranslationResult(
    bool IsSuccess,
    string Text,
    string? ErrorMessage)
{
    public static MacTranslationResult Failure(string message) =>
        new(false, string.Empty, message);
}

internal sealed class MacTranslationService : IDisposable
{
    private readonly Func<MacSettings> _settings;
    private readonly MacOfflineTranslationService _offline;
    private readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromMinutes(2),
    };

    public MacTranslationService(
        Func<MacSettings> settings,
        MacKeychainCredentialStore credentials)
    {
        _settings = settings;
        _offline = new MacOfflineTranslationService(settings);
    }

    public async Task<MacTranslationResult> TranslateAsync(
        string text,
        CancellationToken cancellationToken = default)
    {
        var settings = _settings();
        if (!string.IsNullOrWhiteSpace(settings.OfflineTranslationConfigPath))
        {
            var offline = await _offline.TranslateAsync(text, cancellationToken);
            if (offline.IsSuccess)
            {
                return offline;
            }
        }

        if (!settings.SendTextToOnlineTranslation)
        {
            return MacTranslationResult.Failure(
                "尚未允许把识别文字发送到在线翻译服务。");
        }

        if (!Uri.TryCreate(settings.TranslationEndpoint, UriKind.Absolute, out var endpoint) ||
            endpoint.Scheme != Uri.UriSchemeHttps)
        {
            return MacTranslationResult.Failure("翻译服务地址必须是 HTTPS 地址。");
        }

        var apiKey = MacKeychainCredentialStore.Load();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return MacTranslationResult.Failure("尚未在 macOS Keychain 中保存翻译密钥。");
        }

        if (string.IsNullOrWhiteSpace(settings.TranslationModel))
        {
            return MacTranslationResult.Failure("尚未配置翻译模型。");
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            request.Content = JsonContent.Create(new
            {
                model = settings.TranslationModel,
                temperature = 0,
                messages = new[]
                {
                    new
                    {
                        role = "system",
                        content = $"Translate the user's text to {settings.TranslationTargetLanguage}. Preserve layout, code, numbers, URLs and proper nouns. Return only the translation.",
                    },
                    new { role = "user", content = text },
                },
            });
            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return MacTranslationResult.Failure(
                    $"翻译服务请求失败（HTTP {(int)response.StatusCode}）：{Limit(body)}");
            }

            using var document = JsonDocument.Parse(body);
            var translated = document.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString();
            return string.IsNullOrWhiteSpace(translated)
                ? MacTranslationResult.Failure("翻译服务未返回内容。")
                : new MacTranslationResult(true, translated.Trim(), null);
        }
        catch (OperationCanceledException)
        {
            return MacTranslationResult.Failure("翻译已取消。");
        }
        catch (HttpRequestException exception)
        {
            return MacTranslationResult.Failure($"无法连接翻译服务：{exception.Message}");
        }
        catch (JsonException)
        {
            return MacTranslationResult.Failure("翻译服务返回了无法识别的内容。");
        }
    }

    private static string Limit(string value)
    {
        var normalized = value.Trim();
        return normalized.Length <= 240 ? normalized : normalized[..240] + "...";
    }

    public void Dispose()
    {
        _offline.Dispose();
        _httpClient.Dispose();
    }
}
