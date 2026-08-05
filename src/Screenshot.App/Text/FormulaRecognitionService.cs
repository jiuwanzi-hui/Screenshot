using System.Drawing.Imaging;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Screenshot.App.Capture;
using Screenshot.App.Core;

namespace Screenshot.App.Text;

public static class FormulaRecognitionService
{
    public static async Task<ContentRecognitionResult> RecognizeAsync(
        CapturedImage image,
        AppSettings settings,
        ITranslationCredentialStore credentialStore,
        HttpClient httpClient,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(credentialStore);
        ArgumentNullException.ThrowIfNull(httpClient);

        var providerId = TranslationProviderFactory.ResolveProviderId(
            settings.TranslationProvider);
        var apiKey = credentialStore.GetApiKey(providerId);
        var endpoint = OpenAiCompatibleEndpointResolver
            .NormalizeChatCompletionsEndpoint(settings.TranslationEndpoint);
        if (string.IsNullOrWhiteSpace(apiKey) ||
            string.IsNullOrWhiteSpace(settings.TranslationModel) ||
            !Uri.TryCreate(endpoint, UriKind.Absolute, out var endpointUri) ||
            endpointUri.Scheme != Uri.UriSchemeHttps)
        {
            return ContentRecognitionResult.Failure(
                "公式识别",
                "公式识别需要在线视觉模型。请先在“翻译”设置中配置支持图片输入的模型、API 地址和密钥。");
        }

        try
        {
            using var stream = new MemoryStream();
            image.Bitmap.Save(stream, ImageFormat.Png);
            var imageUrl = $"data:image/png;base64,{Convert.ToBase64String(stream.ToArray())}";
            using var request = new HttpRequestMessage(HttpMethod.Post, endpointUri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            request.Content = JsonContent.Create(new
            {
                model = settings.TranslationModel,
                temperature = 0,
                messages = new object[]
                {
                    new
                    {
                        role = "system",
                        content = "You transcribe mathematical formulas from images. Return only valid LaTeX. " +
                                  "Do not solve, translate, explain, or wrap the answer in Markdown fences. " +
                                  "Preserve line breaks when the image contains multiple formulas. " +
                                  "If the image contains no mathematical formula, return exactly NO_FORMULA.",
                    },
                    new
                    {
                        role = "user",
                        content = new object[]
                        {
                            new
                            {
                                type = "text",
                                text = "Transcribe every mathematical formula in this image into LaTeX.",
                            },
                            new
                            {
                                type = "image_url",
                                image_url = new { url = imageUrl },
                            },
                        },
                    },
                },
            });

            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            timeoutSource.CancelAfter(TimeSpan.FromSeconds(35));
            using var response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeoutSource.Token);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return ContentRecognitionResult.Failure(
                    "公式识别",
                    "视觉模型拒绝了 API 密钥，请检查当前厂商配置。");
            }

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(timeoutSource.Token);
                var detail = OpenAiCompatibleTranslationProvider.ExtractProviderError(body);
                return ContentRecognitionResult.Failure(
                    "公式识别",
                    string.IsNullOrWhiteSpace(detail)
                        ? $"视觉模型请求失败（HTTP {(int)response.StatusCode}），当前模型可能不支持图片输入。"
                        : $"视觉模型请求失败：{detail}");
            }

            using var document = JsonDocument.Parse(
                await response.Content.ReadAsStreamAsync(timeoutSource.Token));
            var latex = document.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString();
            latex = StripCodeFence(latex);
            if (string.Equals(latex, "NO_FORMULA", StringComparison.OrdinalIgnoreCase))
            {
                return ContentRecognitionResult.Failure(
                    "公式识别",
                    "当前选区没有识别到数学公式。");
            }
            return string.IsNullOrWhiteSpace(latex)
                ? ContentRecognitionResult.Failure(
                    "公式识别",
                    "视觉模型没有返回公式内容，请确认所选模型支持图片输入。")
                : new ContentRecognitionResult(true, "公式识别（LaTeX）", latex);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ContentRecognitionResult.Failure(
                "公式识别",
                "公式识别等待超过 35 秒，已停止本次请求。");
        }
        catch (OperationCanceledException)
        {
            return ContentRecognitionResult.Failure("公式识别", "公式识别已取消。");
        }
        catch (HttpRequestException)
        {
            return ContentRecognitionResult.Failure("公式识别", "无法连接视觉模型服务。");
        }
        catch (Exception)
        {
            return ContentRecognitionResult.Failure(
                "公式识别",
                "视觉模型返回了无法解析的内容，请更换支持图片输入的模型。");
        }
    }

    private static string StripCodeFence(string? value)
    {
        var text = value?.Trim() ?? string.Empty;
        if (!text.StartsWith("```", StringComparison.Ordinal))
        {
            return text;
        }

        var firstLineEnd = text.IndexOf('\n');
        var lastFence = text.LastIndexOf("```", StringComparison.Ordinal);
        return firstLineEnd >= 0 && lastFence > firstLineEnd
            ? text[(firstLineEnd + 1)..lastFence].Trim()
            : text;
    }
}
