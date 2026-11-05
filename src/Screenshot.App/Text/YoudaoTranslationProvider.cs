using System.Globalization;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Screenshot.App.Text;

/// <summary>Official Youdao batch NMT API. Only requested text is sent, never the screenshot.</summary>
public sealed class YoudaoTranslationProvider(string appId, string? secret, HttpClient client) : ITranslationProvider
{
    public const string ProviderId = "Youdao";
    public const string Endpoint = "https://openapi.youdao.com/v2/api";
    internal const int MaximumBatchCharacters = 4500; // Official limit: 5000.
    public string Id => ProviderId;

    public async Task<TranslationResult> TranslateAsync(string text, string sourceLanguage, string targetLanguage,
        CancellationToken cancellationToken = default)
    {
        var result = await TranslateSegmentsAsync([text], sourceLanguage, targetLanguage, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess && result.FailedSegmentIndices is not { Count: > 0 }
            ? new(true, result.Segments[0], null) : TranslationResult.Failure(result.ErrorMessage ?? "有道翻译未完成。");
    }

    public async Task<TranslationSegmentsResult> TranslateSegmentsAsync(IReadOnlyList<string> segments,
        string sourceLanguage, string targetLanguage, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(segments);
        if (segments.Count == 0 || segments.All(string.IsNullOrWhiteSpace))
            return TranslationSegmentsResult.Failure("没有可翻译的文字。");
        if (string.IsNullOrWhiteSpace(appId) || string.IsNullOrWhiteSpace(secret))
            return TranslationSegmentsResult.Failure("请填写有道应用 ID 和应用密钥，并在有道智云开通批量文本翻译服务。");

        var source = sourceLanguage == "auto" && segments.Any(text => text.Any(c => c is >= '\u3040' and <= '\u30ff'))
            ? "ja" : sourceLanguage;
        var parts = segments.SelectMany((text, index) => TranslationTargetLanguageMatcher.ShouldTranslate(text, source, targetLanguage)
            ? LocalLargeModelTranslationProvider.SplitText(TranslationTextNormalizer.Normalize(text), MaximumBatchCharacters).Select(part => (Index: index, Text: part))
            : []).ToArray();
        var unique = parts.Select(part => part.Text).Distinct(StringComparer.Ordinal).ToArray();
        var translated = new Dictionary<string, string>(StringComparer.Ordinal);
        var errors = new List<string>();
        for (var start = 0; start < unique.Length;)
        {
            var end = start;
            var characters = 0;
            while (end < unique.Length && end - start < 64 && characters + unique[end].Length <= MaximumBatchCharacters)
                characters += unique[end++].Length;
            var batch = unique[start..end];
            start = end;
            try
            {
                var salt = Guid.NewGuid().ToString("N");
                var time = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
                var fields = new List<KeyValuePair<string, string>>
                {
                    new("appKey", appId.Trim()), new("salt", salt), new("curtime", time), new("signType", "v3"),
                    new("sign", CreateSignature(appId.Trim(), batch, salt, time, secret.Trim())),
                    new("from", MapLanguage(source)), new("to", MapLanguage(targetLanguage)),
                    new("detectLevel", "0"), new("detectFilter", "false"),
                };
                fields.AddRange(batch.Select(text => new KeyValuePair<string, string>("q", text)));
                using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint) { Content = new FormUrlEncodedContent(fields) };
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    errors.Add($"有道翻译请求失败（HTTP {(int)response.StatusCode}）。");
                    break;
                }
                using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken)
                    .ConfigureAwait(false), cancellationToken: cancellationToken).ConfigureAwait(false);
                var root = json.RootElement;
                var code = root.TryGetProperty("errorCode", out var error) ? error.ToString() : "unknown";
                if (code != "0") errors.Add(DescribeError(code));
                if (!root.TryGetProperty("translateResults", out var results) || results.ValueKind != JsonValueKind.Array)
                {
                    if (code == "0") errors.Add("有道未返回分段译文。");
                    break;
                }
                // Match the echoed source, not the response position: failed or
                // reordered entries must never shift translations to another row.
                var requested = batch.ToHashSet(StringComparer.Ordinal);
                foreach (var result in results.EnumerateArray())
                {
                    if (!result.TryGetProperty("query", out var query) || query.ValueKind != JsonValueKind.String ||
                        !result.TryGetProperty("translation", out var output) || output.ValueKind != JsonValueKind.String)
                        continue;
                    var text = query.GetString()!;
                    if (!requested.Remove(text)) continue;
                    var value = output.GetString()!;
                    var failure = TranslationCompletenessValidator.Validate(text, value, targetLanguage);
                    if (failure is null && OrderedTranslationProvider.HasPlausibleTargetLanguage([text], [value], targetLanguage))
                        translated[text] = value;
                    else errors.Add("有道本次译文质量不达标：" + (failure ?? "返回的内容不是目标语言。"));
                }
            }
            catch (HttpRequestException) { errors.Add("无法连接有道翻译服务。"); break; }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            { errors.Add("有道翻译响应超时。"); break; }
            catch (Exception exception) when (exception is JsonException or InvalidOperationException)
            { errors.Add("有道返回了无法识别的响应。"); break; }
        }
        var combined = segments.ToArray();
        var failed = new List<int>();
        foreach (var group in parts.GroupBy(part => part.Index))
        {
            if (group.All(part => translated.ContainsKey(part.Text)))
                combined[group.Key] = string.Join("\n", group.Select(part => translated[part.Text]));
            else failed.Add(group.Key);
        }
        return new TranslationSegmentsResult(true, combined, failed.Count == 0 ? null :
            $"有道翻译未完成 {failed.Count} 段，已保留原文。" + string.Join("；", errors.Distinct(StringComparer.Ordinal)))
        {
            FailedSegmentIndices = failed,
            Notice = translated.Count > 0 ? "实际使用：有道智云批量文本翻译（NMT）。" : "已尝试：有道智云批量文本翻译（NMT）。",
        };
    }

    internal static string CreateSignature(string id, IReadOnlyList<string> text, string salt, string time, string key)
    {
        // Follow the official C# v3 batch example: concatenate q values before truncating.
        var joined = string.Concat(text);
        var input = joined.Length <= 20 ? joined : joined[..10] + joined.Length.ToString(CultureInfo.InvariantCulture) + joined[^10..];
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id + input + salt + time + key)));
    }

    internal static string MapLanguage(string language) => language.Replace('_', '-').ToLowerInvariant() switch
    {
        "zh" or "zh-hans" or "zh-cn" or "zh-chs" => "zh-CHS",
        "zh-hant" or "zh-tw" or "zh-hk" or "zh-cht" => "zh-CHT",
        "auto" or "" => "auto",
        var code => code.Split('-')[0],
    };

    private static string DescribeError(string code) => code switch
    {
        "108" or "202" => "有道拒绝了凭据，请检查应用 ID 和应用密钥。",
        "110" => "有道应用未绑定批量文本翻译服务，请在有道智云控制台开通并绑定。",
        "401" => "有道账户余额不足，请检查账户额度。",
        "411" or "412" => "有道访问频率受限，请稍后重试或检查服务配额。",
        "102" => "有道不支持当前语言，请选择其他翻译服务。",
        "206" => "有道校验时间戳失败，请检查电脑时间。",
        _ => $"有道翻译服务失败（错误码 {code}）。",
    };
}
