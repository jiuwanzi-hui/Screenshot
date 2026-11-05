using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Screenshot.App.Text;

namespace Screenshot.App.Tests;

public sealed class ModelDownloadReliabilityTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "SnapCut-download-tests", Guid.NewGuid().ToString("N"));
    private static readonly byte[] Bytes = "verified offline model bytes"u8.ToArray();
    private static DownloadableModelFile FileDescriptor(string name = "model.gguf") => new("测试模型", name,
        Bytes.Length, Convert.ToHexString(SHA256.HashData(Bytes)),
        ["https://first.example/file", "https://second.example/file"]);

    [Fact]
    public async Task AStalledConnectionMovesToTheNextSource()
    {
        var hosts = new List<string>();
        using var client = new HttpClient(new Handler(async (request, token) =>
        {
            hosts.Add(request.RequestUri!.Host);
            if (hosts.Count == 1) await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(Bytes) };
        }));
        var downloader = new ResumableModelDownloader(client, TimeSpan.FromMilliseconds(40));
        await downloader.DownloadAsync([FileDescriptor()], _directory, null, CancellationToken.None);
        Assert.Equal(["first.example", "second.example"], hosts);
        Assert.Equal(Bytes, await File.ReadAllBytesAsync(Path.Combine(_directory, "model.gguf")));
    }

    [Fact]
    public async Task AStalledBodyResumesFromItsLastWrittenByteOnTheNextSource()
    {
        long? resumedAt = null;
        using var client = new HttpClient(new Handler((request, _) =>
        {
            if (request.RequestUri!.Host == "first.example")
            {
                var content = new StreamContent(new StalledStream(Bytes[..7]));
                content.Headers.ContentLength = Bytes.Length;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
            }
            resumedAt = request.Headers.Range!.Ranges.Single().From;
            var start = resumedAt ?? throw new InvalidOperationException("续传请求缺少起点");
            var remaining = new ByteArrayContent(Bytes[(int)start..]);
            remaining.Headers.ContentRange = new ContentRangeHeaderValue(start, Bytes.Length - 1, Bytes.Length);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = remaining });
        }));
        var downloader = new ResumableModelDownloader(client, readIdleTimeout: TimeSpan.FromMilliseconds(40));
        await downloader.DownloadAsync([FileDescriptor()], _directory, null, CancellationToken.None);
        Assert.Equal(7, resumedAt);
        Assert.Equal(Bytes, await File.ReadAllBytesAsync(Path.Combine(_directory, "model.gguf")));
    }

    [Fact]
    public async Task CancellationPreservesThePartialFileWithoutTryingAnotherSource()
    {
        var requests = 0;
        using var client = new HttpClient(new Handler((_, _) =>
        {
            requests++;
            var content = new StreamContent(new StalledStream(Bytes[..7]));
            content.Headers.ContentLength = Bytes.Length;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(80));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ResumableModelDownloader(client)
            .DownloadAsync([FileDescriptor()], _directory, null, cancellation.Token));
        Assert.Equal(1, requests);
        Assert.Equal(Bytes[..7], await File.ReadAllBytesAsync(Path.Combine(_directory, "model.gguf.part")));
    }

    [Fact]
    public async Task ACorruptSameSizeInstalledFileIsRepaired()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllBytesAsync(Path.Combine(_directory, "model.gguf"), new byte[Bytes.Length]);
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new ByteArrayContent(Bytes) })));
        await new ResumableModelDownloader(client).DownloadAsync([FileDescriptor()], _directory, null, CancellationToken.None);
        Assert.Equal(Bytes, await File.ReadAllBytesAsync(Path.Combine(_directory, "model.gguf")));
    }

    [Fact]
    public async Task ARejectedHashSourceIsNotDownloadedAgain()
    {
        var hosts = new List<string>();
        var descriptor = FileDescriptor() with { DownloadUrls = ["https://first.example/file", "https://second.example/file", "https://third.example/file"] };
        using var client = new HttpClient(new Handler((request, _) =>
        {
            var host = request.RequestUri!.Host;
            hosts.Add(host);
            return Task.FromResult(new HttpResponseMessage(host == "second.example" ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)
            { Content = new ByteArrayContent(host == "first.example" ? new byte[Bytes.Length] : Bytes) });
        }));
        await new ResumableModelDownloader(client).DownloadAsync([descriptor], _directory, null, CancellationToken.None);
        Assert.Equal(["first.example", "second.example", "third.example"], hosts);
    }

    [Fact]
    public async Task LocalImportValidatesAndInstallsWithoutAnHttpClient()
    {
        var source = Path.Combine(_directory, "source");
        Directory.CreateDirectory(source);
        var file = Path.Combine(source, "model.gguf");
        await File.WriteAllBytesAsync(file, Bytes);
        await ResumableModelDownloader.ImportAsync([FileDescriptor()], _directory, [file], null, CancellationToken.None);
        Assert.Equal(Bytes, await File.ReadAllBytesAsync(Path.Combine(_directory, "model.gguf")));
        Assert.Equal(Bytes, await File.ReadAllBytesAsync(file));
        Assert.Empty(Directory.GetFiles(_directory, "*.import-*"));
    }

    [Fact]
    public async Task InvalidImportLeavesAllExistingFilesUntouched()
    {
        var source = Path.Combine(_directory, "source");
        Directory.CreateDirectory(source);
        var model = Path.Combine(source, "model.gguf");
        var runtime = Path.Combine(source, "runtime.zip");
        await File.WriteAllBytesAsync(model, Bytes);
        await File.WriteAllBytesAsync(runtime, new byte[Bytes.Length]);
        var installed = Path.Combine(_directory, "model.gguf");
        await File.WriteAllTextAsync(installed, "existing version");
        await Assert.ThrowsAsync<InvalidDataException>(() => ResumableModelDownloader.ImportAsync(
            [FileDescriptor(), FileDescriptor("runtime.zip")], _directory, [model, runtime], null, CancellationToken.None));
        Assert.Equal("existing version", await File.ReadAllTextAsync(installed));
        Assert.False(File.Exists(Path.Combine(_directory, "runtime.zip")));
        Assert.Empty(Directory.GetFiles(_directory, "*.import-*"));
    }

    [Fact]
    public async Task AnIncorrectPartialRangeCannotBePromoted()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllBytesAsync(Path.Combine(_directory, "model.gguf.part"), Bytes[..7]);
        var requests = 0;
        using var client = new HttpClient(new Handler((request, _) =>
        {
            requests++;
            var content = new ByteArrayContent(Bytes[7..]);
            content.Headers.ContentRange = new ContentRangeHeaderValue(
                request.RequestUri!.Host == "first.example" ? 8 : 7, Bytes.Length - 1, Bytes.Length);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = content });
        }));
        await new ResumableModelDownloader(client).DownloadAsync([FileDescriptor()], _directory, null, CancellationToken.None);
        Assert.Equal(2, requests);
        Assert.Equal(Bytes, await File.ReadAllBytesAsync(Path.Combine(_directory, "model.gguf")));
    }

    [BundledRuntimeFact]
    public async Task BundledRuntimeCanBeInstalledWithoutNetworkOrAModelDownload()
    {
        using var client = new HttpClient(new Handler((_, _) => throw new InvalidOperationException("本地导入不得联网")));
        using var manager = new LocalLargeTranslationModelManager(_directory, client);
        Assert.Equal(2_489_909_312, manager.GetStatus().DownloadSize);
        var bundle = Path.Combine(AppContext.BaseDirectory, "OfflinePackages", "llama-b10276-bin-win-vulkan-x64.zip");
        var result = await manager.ImportAsync([bundle], null, CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.NotNull(manager.ExecutablePath);
        Assert.False(manager.GetStatus().IsInstalled); // Still needs the model, never reported ready.
        Assert.Contains("还缺少", result.ErrorMessage);
    }

    private sealed class BundledRuntimeFactAttribute : FactAttribute
    {
        public BundledRuntimeFactAttribute()
        {
            if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "OfflinePackages", "llama-b10276-bin-win-vulkan-x64.zip")))
                Skip = "Run installer/prepare-translation-runtime.ps1 before validating the bundled runtime.";
        }
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => handler(request, cancellationToken);
    }

    private sealed class StalledStream(byte[] prefix) : Stream
    {
        private bool _read;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_read) { _read = true; prefix.CopyTo(buffer); return prefix.Length; }
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
