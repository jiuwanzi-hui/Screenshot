using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Screenshot.App.Text;

/// <summary>Official Baidu general translation API; credentials belong to each user profile.</summary>
public sealed class BaiduTranslationProvider(string appId, string? secret, HttpClient client, bool advanced = false) : ITranslationProvider
{
    public const string ProviderId = "Baidu";
    public const string Endpoint = "https://fanyi-api.baidu.com/api/trans/vip/translate";
    public const string LargeModelProviderId = "BaiduLargeModel";
    public const string LargeModelEndpoint = "https://fanyi-api.baidu.com/ait/api/aiTextTranslate";
    internal bool UseLargeModel { get; init; }
    internal TranslationProfileHealth.Observer? AvailabilityObserver { get; init; }
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "Per-account limiter lives for the application lifetime; shared across overlapping requests.")]
    private sealed class RateLimit
    {
        internal readonly SemaphoreSlim Gate = new(1, 1);
        internal long LastRequest;
    }
    private static readonly ConcurrentDictionary<string, RateLimit> Limits = new(StringComparer.Ordinal);
    public string Id => UseLargeModel ? LargeModelProviderId : ProviderId;

    public async Task<TranslationResult> TranslateAsync(string text, string sourceLanguage, string targetLanguage,
        CancellationToken cancellationToken = default)
    {
        var result = await TranslateSegmentsAsync([text], sourceLanguage, targetLanguage, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess && result.FailedSegmentIndices is not { Count: > 0 }
            ? new(true, result.Segments[0], null) : TranslationResult.Failure(result.ErrorMessage ?? "百度翻译未完成。");
    }

    public async Task<TranslationSegmentsResult> TranslateSegmentsAsync(IReadOnlyList<string> segments,
        string sourceLanguage, string targetLanguage, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(appId) || string.IsNullOrWhiteSpace(secret))
            return TranslationSegmentsResult.Failure(UseLargeModel
                ? "请填写百度 APP ID 和 API Key，并开通大模型文本翻译服务。"
                : "请填写百度 APP ID 和密钥，并开通通用文本翻译服务。");
        if (segments.Count == 0) return TranslationSegmentsResult.Failure("没有可翻译的文字。");
        // Split at original newlines because Baidu returns one result per line.
        var requestId = AvailabilityObserver?.BeginRequest() ?? 0;
        var maximum = UseLargeModel || advanced ? 1900 : 900;
        var parts = segments.SelectMany((text, index) => TranslationTargetLanguageMatcher.ShouldTranslate(text, sourceLanguage, targetLanguage)
            ? text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .SelectMany(line => LocalLargeModelTranslationProvider.SplitText(TranslationTextNormalizer.Normalize(line), maximum))
                .Select(part => (Index: index, Text: part)) : []).ToArray();
        var unique = parts.Select(part => part.Text).Distinct(StringComparer.Ordinal).ToArray();
        var translated = new Dictionary<string, string>(StringComparer.Ordinal);
        var errors = new List<string>();
        for (var start = 0; start < unique.Length;)
        {
            var end = start;
            var length = 0;
            while (end < unique.Length && length + unique[end].Length + 1 <= maximum + 1)
                length += unique[end++].Length + 1;
            var batch = unique[start..end];
            start = end;
            var query = string.Join("\n", batch);
            try
            {
                var limit = Limits.GetOrAdd(Id + ":" + appId.Trim(), _ => new RateLimit());
                await limit.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    var delay = (!UseLargeModel && advanced ? 110 : 1050) - (Environment.TickCount64 - limit.LastRequest);
                    if (delay > 0) await Task.Delay(TimeSpan.FromMilliseconds(delay), cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    limit.LastRequest = Environment.TickCount64;
                }
                finally { limit.Gate.Release(); }
                var salt = Guid.NewGuid().ToString("N");
                using var request = new HttpRequestMessage(HttpMethod.Post, UseLargeModel ? LargeModelEndpoint : Endpoint)
                {
                    Content = UseLargeModel ? JsonContent.Create(new Dictionary<string, string>
                    {
                        ["q"] = query, ["appid"] = appId.Trim(), ["model_type"] = "llm",
                        ["from"] = MapLanguage(sourceLanguage), ["to"] = MapLanguage(targetLanguage),
                    }) : new FormUrlEncodedContent(new Dictionary<string, string>
                    {
                        ["q"] = query, ["appid"] = appId.Trim(), ["salt"] = salt,
                        ["from"] = MapLanguage(sourceLanguage), ["to"] = MapLanguage(targetLanguage),
                        ["sign"] = CreateSignature(appId.Trim(), query, salt, secret.Trim()),
                    }),
                };
                if (UseLargeModel) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret.Trim());
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) { errors.Add($"百度翻译请求失败（HTTP {(int)response.StatusCode}）。"); break; }
                using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken)
                    .ConfigureAwait(false), cancellationToken: cancellationToken).ConfigureAwait(false);
                var root = json.RootElement;
                if (root.TryGetProperty("error_code", out var code) && code.ToString() is not "0" and not "52000")
                {
                    errors.Add(UseLargeModel && code.ToString() is "58002" or "90107"
                        ? "请在百度翻译开放平台开通大模型文本翻译服务。"
                        : DescribeError(code.ToString())); break;
                }
                if (!root.TryGetProperty("trans_result", out var results) || results.ValueKind != JsonValueKind.Array)
                { errors.Add("百度未返回分段译文。"); break; }
                var requested = batch.ToHashSet(StringComparer.Ordinal);
                foreach (var item in results.EnumerateArray())
                {
                    if (!item.TryGetProperty("src", out var src) || src.ValueKind != JsonValueKind.String ||
                        !item.TryGetProperty("dst", out var dst) || dst.ValueKind != JsonValueKind.String) continue;
                    var original = src.GetString()!;
                    if (!requested.Remove(original)) continue;
                    var output = dst.GetString()!;
                    var failure = TranslationCompletenessValidator.Validate(original, output, targetLanguage);
                    if (failure is null && OrderedTranslationProvider.HasPlausibleTargetLanguage([original], [output], targetLanguage))
                        translated[original] = output;
                    else errors.Add("百度本次译文质量不达标：" + (failure ?? "返回的内容不是目标语言。"));
                }
            }
            catch (HttpRequestException) { errors.Add("无法连接百度翻译服务。"); break; }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            { errors.Add("百度翻译响应超时。"); break; }
            catch (Exception exception) when (exception is JsonException or InvalidOperationException)
            { errors.Add("百度返回了无法识别的响应。"); break; }
        }
        var combined = segments.ToArray();
        var failed = new List<int>();
        foreach (var group in parts.GroupBy(part => part.Index))
        {
            if (group.All(part => translated.ContainsKey(part.Text)))
                combined[group.Key] = string.Join("\n", group.Select(part => translated[part.Text]));
            else failed.Add(group.Key);
        }
        var error = failed.Count == 0 ? null : errors.Count == 0 ? "百度部分段落未返回完整译文。" : string.Join("\n", errors.Distinct());
        if (parts.Length > 0) AvailabilityObserver?.Complete(requestId, failed.Count == 0, error ?? "最近一次百度翻译成功。");
        return new(true, combined, error) { FailedSegmentIndices = failed };
    }

    // MD5 is required by the service authentication protocol, not used for password storage.
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5351", Justification = "Baidu official API requires MD5 authentication over HTTPS.")]
    internal static string CreateSignature(string id, string query, string salt, string key) =>
        Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(id + query + salt + key))).ToLowerInvariant();

    internal static string MapLanguage(string language) => TranslationLanguageCatalog.NormalizeOfflineCode(language) switch
    {
        "zh" => "zh", "zh_hant" => "cht",
        "ja" => "jp", "ko" => "kor", "fr" => "fra", "es" => "spa", "ar" => "ara",
        "az" => "aze", "bg" => "bul", "bn" => "ben", "bs" => "bos", "ca" => "cat",
        "da" => "dan", "et" => "est", "eu" => "baq", "fa" => "per", "fi" => "fin",
        "gl" => "glg", "gu" => "guj", "he" => "heb", "hr" => "hrv", "is" => "ice",
        "kn" => "kan", "lt" => "lit", "lv" => "lav", "ml" => "mal", "ms" => "may",
        "nb" => "nob", "no" => "nor", "nn" => "nno", "ro" => "rom", "sl" => "slo",
        "sq" => "alb", "sr" => "srp", "sv" => "swe", "ta" => "tam", "te" => "tel",
        "uk" => "ukr", "vi" => "vie", "be" => "bel", "hbs" => "sec", "mr" => "mar", "ur" => "urd",
        null => "auto", var code => code,
    };
    private static string DescribeError(string code) => code switch
    {
        "52003" or "54001" => "百度凭据校验失败，请检查 APP ID 和密钥。",
        "54003" => "百度请求频率受限，请核对开通的服务版本。",
        "54004" => "百度账户额度或余额不足，请检查账户。",
        "58001" => "该语言未在当前百度服务版本中开放或不受支持，请核对服务版本或选择其他翻译方式。",
        "58002" or "90107" => "请在百度翻译开放平台开通通用文本翻译服务。",
        _ => $"百度翻译服务失败（错误码 {code}）。",
    };
}
