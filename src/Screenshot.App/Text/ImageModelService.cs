using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Windows.Media.Imaging;
using Screenshot.App.Core;

namespace Screenshot.App.Text;

// OpenAI-compatible Image API. Preserve the configured model, including gateway aliases.
internal static class ImageModelService
{
    internal static Uri? Endpoint(string value, bool edit)
    {
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) ||
            uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback)) return null;
        var path = uri.AbsolutePath.TrimEnd('/');
        foreach (var suffix in new[] { "/chat/completions", "/responses", "/images/generations", "/images/edits", "/models" })
            if (path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) { path = path[..^suffix.Length]; break; }
        if (path.Length == 0) path = "/v1";
        return new UriBuilder(uri) { Path = path + (edit ? "/images/edits" : "/images/generations"), Fragment = "" }.Uri;
    }

    internal static async Task<ImageQuestionReply> ProcessAsync(AiTranslationProfile profile, string? key, HttpClient client,
        IReadOnlyList<byte[]> images, IReadOnlyList<ImageQuestionTurn> history, string question, CancellationToken token, string strength = "auto", bool updateProfileHealth = true)
    {
        if (string.IsNullOrWhiteSpace(key)) return new(false, "", "请先配置图片模型的 API Key。");
        if (string.IsNullOrWhiteSpace(question)) return new(false, "", "请输入图片处理要求。");
        var endpoint = Endpoint(profile.Endpoint, images.Count > 0);
        if (endpoint is null) return new(false, "", "请配置有效的 HTTPS 图片服务地址。");
        if (images.Count > 5 || images.Sum(p => (long)p.Length) > 64 * 1024 * 1024 || images.Any(p => p.Length is 0 or > 32 * 1024 * 1024))
            return new(false, "", "最多五张图片，每张不超过 32 MB，合计不超过 64 MB。");
        var prompt = history.Count == 0 ? question : string.Join("\n", history.Select(h => "Earlier request: " + h.Question)) + "\nCurrent request (takes precedence): " + question;
        if (prompt.Length > 32_000) return new(false, "", "图片处理上下文过长，请打开新对话。");
        var health = updateProfileHealth ? TranslationProfileHealth.Observe(profile, key) : null; var id = health?.BeginRequest() ?? 0;
        ImageQuestionReply Failure(string reason) { health?.Complete(id, false, reason); return new(false, "", reason); }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromMinutes(3));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            if (images.Count == 0)
            {
                var body = new Dictionary<string, object> { ["model"] = profile.Model, ["prompt"] = prompt, ["n"] = 1 };
                if (strength != "auto") body["quality"] = strength;
                request.Content = JsonContent.Create(body);
            }
            else
            {
                var body = new MultipartFormDataContent(); request.Content = body;
                body.Add(new StringContent(profile.Model), "model"); body.Add(new StringContent(prompt), "prompt"); body.Add(new StringContent("1"), "n");
                if (strength != "auto") body.Add(new StringContent(strength), "quality");
                for (var i = 0; i < images.Count; i++)
                {
                    var image = new ByteArrayContent(images[i]); image.Headers.ContentType = new MediaTypeHeaderValue("image/png");
                    body.Add(image, images.Count == 1 ? "image" : "image[]", $"image-{i + 1}.png");
                }
            }
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            var bytes = await ReadBoundedAsync(response, response.IsSuccessStatusCode ? 48 * 1024 * 1024 : 64 * 1024, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var detail = OpenAiCompatibleTranslationProvider.ExtractProviderError(System.Text.Encoding.UTF8.GetString(bytes)) ?? "";
                detail = detail.Replace(key, "[已隐藏]", StringComparison.Ordinal);
                var reason = $"图片接口请求失败（HTTP {(int)response.StatusCode}）" + (detail.Length > 0 ? "：" + detail[..Math.Min(600, detail.Length)] : "。请核对模型用途、接口权限和账户额度。");
                health?.Complete(id, false, reason); return new(false, "", reason);
            }
            using var document = JsonDocument.Parse(bytes);
            if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array || data.GetArrayLength() == 0)
                return Failure("图片接口没有返回图片，不能判定测试成功。请核对该服务的图像接口格式。");
            // n=1; reject unexpected fan-out instead of retaining unbounded decoded images.
            if (data.GetArrayLength() > 1) return Failure("图片接口返回了超出请求数量的图片，请检查服务配置。");
            var entry = data[0]; byte[] pixels;
            if (entry.TryGetProperty("b64_json", out var encoded) && encoded.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(encoded.GetString()))
                pixels = Convert.FromBase64String(encoded.GetString()!);
            else if (entry.TryGetProperty("url", out var address) && Uri.TryCreate(address.GetString(), UriKind.Absolute, out var url) && url.Scheme == "https")
            {
                using var download = new HttpRequestMessage(HttpMethod.Get, url); // Never forward the model credential to the image host.
                using var downloaded = await client.SendAsync(download, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                downloaded.EnsureSuccessStatusCode(); pixels = await ReadBoundedAsync(downloaded, 32 * 1024 * 1024, timeout.Token).ConfigureAwait(false);
            }
            else return Failure("图片接口返回的图片地址或编码无效。");
            var bitmap = Decode(pixels);
            token.ThrowIfCancellationRequested();
            health?.Complete(id, true, "图片处理请求成功，已验证返回的图片。");
            return new(true, "图片处理完成。可放大查看、保存，或添加到本轮图片继续处理。") { Images = [bitmap] };
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { health?.Complete(id, false, "图片处理超时。"); return new(false, "", "图片处理超时，请稍后重试。"); }
        catch (HttpRequestException) { health?.Complete(id, false, "图片服务连接失败。"); return new(false, "", "图片服务连接或图片下载失败，请检查网络后重试。"); }
        catch (Exception e) when (e is JsonException or FormatException or IOException or NotSupportedException or ArgumentException or InvalidOperationException)
        { return Failure(e is InvalidOperationException ? e.Message : "服务返回的图片无法解析，请检查模型用途和接口格式。"); }
    }

    internal static BitmapSource Decode(byte[] bytes)
    {
        if (bytes.Length > 32 * 1024 * 1024) throw new InvalidOperationException("模型返回的图片超过 32 MB。");
        using var stream = new MemoryStream(bytes, writable: false);
        var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnDemand).Frames[0];
        if ((long)frame.PixelWidth * frame.PixelHeight > 40_000_000) throw new InvalidOperationException("模型返回的图片超过 4000 万像素。");
        var image = new WriteableBitmap(frame); image.Freeze(); return image;
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpResponseMessage response, int limit, CancellationToken token)
    {
        if (response.Content.Headers.ContentLength > limit) throw new InvalidOperationException("图片服务响应超过大小限制。");
        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var buffer = new MemoryStream(); var block = new byte[16384]; int count;
        while ((count = await stream.ReadAsync(block, token).ConfigureAwait(false)) > 0)
        { if (buffer.Length + count > limit) throw new InvalidOperationException("图片服务响应超过大小限制。"); buffer.Write(block, 0, count); }
        return buffer.ToArray();
    }
}
