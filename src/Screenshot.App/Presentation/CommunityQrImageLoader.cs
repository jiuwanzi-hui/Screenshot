using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Media.Imaging;

namespace Screenshot.App.Presentation;

internal sealed class CommunityQrImageLoader(HttpClient client, string cachePath, byte[] bundled)
{
    internal const string ImageUrl = "https://raw.githubusercontent.com/jiuwanzi-hui/Screenshot/main/src/Screenshot.App/Assets/SnapCutCommunityQr.jpg";
    private const int MaximumBytes = 2 * 1024 * 1024;
    private readonly string _bundledHash = Convert.ToHexString(SHA256.HashData(bundled));
    internal sealed record Result(BitmapImage Image, string Status);
    private sealed record Cache(string BundledHash, byte[] Bytes, DateTimeOffset CheckedAt);

    internal async Task<Result> LoadFallbackAsync(CancellationToken token)
    {
        try
        {
            if (File.Exists(cachePath) && new FileInfo(cachePath).Length <= MaximumBytes * 2)
            {
                var cache = JsonSerializer.Deserialize<Cache>(await File.ReadAllBytesAsync(cachePath, token).ConfigureAwait(false));
                // A new bundled QR must supersede an old installation's cache.
                if (cache?.BundledHash == _bundledHash && cache.Bytes.Length <= MaximumBytes)
                    return new(await DecodeAsync(cache.Bytes, token).ConfigureAwait(false),
                        $"显示最近成功获取的二维码（{cache.CheckedAt.ToLocalTime():MM-dd HH:mm}）。");
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or NotSupportedException or ArgumentException or FormatException or System.Runtime.InteropServices.COMException) { }
        return new(await DecodeAsync(bundled, token).ConfigureAwait(false), "显示本版本内置二维码。");
    }

    internal async Task<Result> RefreshAsync(CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        using var request = new HttpRequestMessage(HttpMethod.Get, ImageUrl + "?refresh=" + Guid.NewGuid().ToString("N"));
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaximumBytes) throw new InvalidDataException("二维码图片过大。");
        await using var source = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var block = new byte[16 * 1024];
        int read;
        while ((read = await source.ReadAsync(block, timeout.Token).ConfigureAwait(false)) != 0)
        {
            if (buffer.Length + read > MaximumBytes) throw new InvalidDataException("二维码图片过大。");
            buffer.Write(block, 0, read);
        }
        var bytes = buffer.ToArray();
        var image = await DecodeAsync(bytes, timeout.Token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        var checkedAt = DateTimeOffset.Now;
        var temporary = cachePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            await File.WriteAllBytesAsync(temporary,
                JsonSerializer.SerializeToUtf8Bytes(new Cache(_bundledHash, bytes, checkedAt)), token).ConfigureAwait(false);
            File.Move(temporary, cachePath, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
        return new(image, $"已获取线上最新图片（{checkedAt:HH:mm}）；有效期以图片为准。");
    }

    private static Task<BitmapImage> DecodeAsync(byte[] bytes, CancellationToken token) => Task.Run(() =>
    {
        token.ThrowIfCancellationRequested();
        using var stream = new MemoryStream(bytes, writable: false);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.DecodePixelWidth = 480;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }, token);
}
