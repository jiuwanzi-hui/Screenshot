using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net;
using System.Net.Http;
using Screenshot.App.Presentation;

namespace Screenshot.App.Tests;

public sealed class CommunityQrImageTests
{
    [Fact]
    public async Task RefreshReplacesCachedImageAndANewBundleInvalidatesOldCache()
    {
        var path = Path.Combine(Path.GetTempPath(), "snapcut-qr-" + Guid.NewGuid().ToString("N"), "cache.json");
        try
        {
            var bundled = Picture(Color.Red);
            var online = Picture(Color.Blue);
            var requests = new List<Uri>();
            using var client = new HttpClient(new Handler((request, _) =>
            {
                requests.Add(request.RequestUri!);
                Assert.True(request.Headers.CacheControl!.NoCache);
                Assert.True(request.Headers.CacheControl.NoStore);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(online) });
            }));
            var loader = new CommunityQrImageLoader(client, path, bundled);
            Assert.Contains("内置", (await loader.LoadFallbackAsync(default)).Status);
            var first = await loader.RefreshAsync(default);
            Assert.Contains("线上最新", first.Status);
            Assert.True(first.Image.IsFrozen);
            var cached = await loader.LoadFallbackAsync(default);
            Assert.Equal(Pixels(first), Pixels(cached));
            online = Picture(Color.Green);
            var second = await loader.RefreshAsync(default);
            Assert.NotEqual(Pixels(first), Pixels(second));
            Assert.NotEqual(requests[0].Query, requests[1].Query);
            Assert.Equal(Pixels(second), Pixels(await loader.LoadFallbackAsync(default)));
            var updated = new CommunityQrImageLoader(client, path, Picture(Color.Yellow));
            var replacement = await updated.LoadFallbackAsync(default);
            Assert.Contains("内置", replacement.Status);
            Assert.NotEqual(Pixels(second), Pixels(replacement));
        }
        finally { if (Directory.Exists(Path.GetDirectoryName(path))) Directory.Delete(Path.GetDirectoryName(path)!, true); }
    }

    [Fact]
    public async Task InvalidDownloadDoesNotOverwriteLastGoodCacheAndCancellationStopsRequest()
    {
        var path = Path.Combine(Path.GetTempPath(), "snapcut-qr-" + Guid.NewGuid().ToString("N"), "cache.json");
        try
        {
            var online = Picture(Color.Blue);
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var wait = false;
            using var client = new HttpClient(new Handler(async (_, token) =>
            {
                if (wait) { started.TrySetResult(); await Task.Delay(Timeout.Infinite, token); }
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(online) };
            }));
            var loader = new CommunityQrImageLoader(client, path, Picture(Color.Red));
            var good = await loader.RefreshAsync(default);
            online = [1, 2, 3];
            await Assert.ThrowsAnyAsync<Exception>(() => loader.RefreshAsync(default));
            Assert.Equal(Pixels(good), Pixels(await loader.LoadFallbackAsync(default)));
            using var cancelled = new CancellationTokenSource();
            wait = true;
            var pending = loader.RefreshAsync(cancelled.Token);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        }
        finally { if (Directory.Exists(Path.GetDirectoryName(path))) Directory.Delete(Path.GetDirectoryName(path)!, true); }
    }

    private static byte[] Picture(Color color)
    {
        using var bitmap = new Bitmap(2, 2);
        using (var graphics = Graphics.FromImage(bitmap)) graphics.Clear(color);
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }

    private static byte[] Pixels(CommunityQrImageLoader.Result value)
    {
        var stride = (value.Image.PixelWidth * value.Image.Format.BitsPerPixel + 7) / 8;
        var bytes = new byte[stride * value.Image.PixelHeight];
        value.Image.CopyPixels(bytes, stride, 0);
        return bytes;
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
