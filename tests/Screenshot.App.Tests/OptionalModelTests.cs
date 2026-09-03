using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Screenshot.App.Core;
using Screenshot.App.Text;

namespace Screenshot.App.Tests;

public sealed class OptionalModelTests : IDisposable
{
    private readonly string _testDirectory = Path.Combine(
        Path.GetTempPath(),
        "Screenshot.App.Tests",
        Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 1)]
    [InlineData(4, 2)]
    [InlineData(8, 2)]
    [InlineData(16, 4)]
    [InlineData(24, 4)]
    [InlineData(64, 4)]
    public void HeavyModelCpuBudgetAdaptsToTheComputer(
        int logicalProcessorCount,
        int expectedThreadCount)
    {
        Assert.Equal(
            expectedThreadCount,
            HeavyWorkloadBudget.CalculateCpuThreadCount(
                logicalProcessorCount));
    }

    [Theory]
    [InlineData(4, 2)]
    [InlineData(8, 6)]
    [InlineData(16, 14)]
    public void BurstCpuBudgetLeavesTwoCoresForTheUi(
        int logicalProcessorCount,
        int expectedThreadCount)
    {
        Assert.Equal(
            expectedThreadCount,
            HeavyWorkloadBudget.CalculateBurstCpuThreadCount(
                logicalProcessorCount));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 1)]
    [InlineData(4, 2)]
    [InlineData(8, 2)]
    [InlineData(12, 4)]
    [InlineData(16, 4)]
    [InlineData(24, 4)]
    public void OcrCpuBudgetStaysBoundedForForegroundRecognition(
        int logicalProcessorCount,
        int expectedThreadCount)
    {
        Assert.Equal(
            expectedThreadCount,
            HeavyWorkloadBudget.CalculateOcrThreadCount(
                logicalProcessorCount));
    }

    [Fact]
    public void OfflineTranslationConfigurationUsesTheSharedCpuBudget()
    {
        var configuration = OfflineTranslationModelCatalog.CreateConfiguration(
            "model.bin",
            "source.spm",
            "target.spm",
            null);

        Assert.Contains(
            $"cpu-threads: {HeavyWorkloadBudget.BurstCpuThreadCount}",
            configuration);
        Assert.DoesNotContain("cpu-threads: 0", configuration);
    }

    [Fact]
    public void ReportsExactHighQualityOcrDownloadSizeWhenNotInstalled()
    {
        using var manager = new HighQualityOcrModelManager(_testDirectory);

        var status = manager.GetStatus();

        Assert.False(status.IsInstalled);
        Assert.Equal(32_257_432, status.DownloadSize);
        Assert.Equal(status.DownloadSize, status.InstalledSize);
    }

    [Fact]
    public void ReportsLargeTranslationModelAndRuntimeDownloadSize()
    {
        using var manager = new LocalLargeTranslationModelManager(_testDirectory);

        var status = manager.GetStatus();

        Assert.False(status.IsInstalled);
        Assert.Equal(1_004_395_773, status.DownloadSize);
        Assert.True(status.InstalledSize > status.DownloadSize);
    }

    [Theory]
    [InlineData("[\"你好\",\"世界\"]", 2, true)]
    [InlineData("startup output\n[\"你好\"]\n", 1, true)]
    [InlineData("[\"只有一项\"]", 2, false)]
    [InlineData("not json", 1, false)]
    public void ParsesOnlyCompleteLargeModelTranslationArrays(
        string output,
        int expectedCount,
        bool expectedSuccess)
    {
        var success = LocalLargeModelTranslationProvider.TryParseTranslations(
            output,
            expectedCount,
            out var translations);

        Assert.Equal(expectedSuccess, success);
        Assert.Equal(expectedSuccess ? expectedCount : 0, translations.Count);
    }

    [Fact]
    public void UsesTheLastValidTranslationArrayAfterTheEchoedPrompt()
    {
        const string output =
            "Segments: [\"bandwidth sharing application\"]\n" +
            "[\"带宽共享应用\"]\n" +
            "[ Prompt: 200 t/s | Generation: 40 t/s ]";

        var success = LocalLargeModelTranslationProvider.TryParseTranslations(
            output,
            expectedCount: 1,
            out var translations);

        Assert.True(success);
        Assert.Equal(["带宽共享应用"], translations);
    }

    [Fact]
    public void ExpandsLargeModelOutputBudgetForLongerText()
    {
        var shortLimit = LocalLargeModelTranslationProvider
            .CalculateOutputTokenLimit(["short"]);
        var longLimit = LocalLargeModelTranslationProvider
            .CalculateOutputTokenLimit([new string('a', 600)]);

        Assert.Equal(512, shortLimit);
        Assert.True(longLimit > shortLimit);
        Assert.InRange(longLimit, 512, 1024);
    }

    [Fact]
    public async Task PromotesACompletePartialFileWithoutRequestingPastItsEnd()
    {
        var content = Encoding.UTF8.GetBytes("complete model content");
        var file = CreateDownloadableFile(content);
        Directory.CreateDirectory(_testDirectory);
        var partialPath = Path.Combine(_testDirectory, file.FileName + ".part");
        await File.WriteAllBytesAsync(partialPath, content);
        using var client = new HttpClient(new RejectingRequestHandler());
        var downloader = new ResumableModelDownloader(client);

        await downloader.DownloadAsync(
            [file],
            _testDirectory,
            progress: null,
            CancellationToken.None);

        Assert.False(File.Exists(partialPath));
        Assert.Equal(
            content,
            await File.ReadAllBytesAsync(Path.Combine(
                _testDirectory,
                file.FileName)));
    }

    [Fact]
    public async Task SendsAProductUserAgentForModelDownloads()
    {
        var content = Encoding.UTF8.GetBytes("downloaded model");
        var file = CreateDownloadableFile(content);
        var handler = new RecordingDownloadHandler(content);
        using var client = new HttpClient(handler);
        var downloader = new ResumableModelDownloader(client);

        await downloader.DownloadAsync(
            [file],
            _testDirectory,
            progress: null,
            CancellationToken.None);

        Assert.StartsWith("SnapCut/", handler.UserAgent);
    }

    [Fact]
    public async Task UsesTheNextMirrorWhenTheFirstCompleteFileFailsHashValidation()
    {
        var content = Encoding.UTF8.GetBytes("verified model");
        var file = new DownloadableModelFile(
            "测试模型",
            "model.bin",
            content.Length,
            Convert.ToHexString(SHA256.HashData(content)),
            ["https://first.example/model.bin", "https://second.example/model.bin"]);
        var handler = new MirrorDownloadHandler(content);
        using var client = new HttpClient(handler);
        var downloader = new ResumableModelDownloader(client);

        await downloader.DownloadAsync(
            [file],
            _testDirectory,
            progress: null,
            CancellationToken.None);

        Assert.Equal(["first.example", "second.example"], handler.RequestHosts);
        Assert.Equal(
            content,
            await File.ReadAllBytesAsync(Path.Combine(_testDirectory, file.FileName)));
    }

    [Fact]
    public async Task ResumesFromBytesWrittenBeforeThePreviousMirrorDisconnected()
    {
        var content = Encoding.UTF8.GetBytes("a model file whose first mirror disconnects");
        var file = new DownloadableModelFile(
            "测试模型",
            "model.bin",
            content.Length,
            Convert.ToHexString(SHA256.HashData(content)),
            ["https://first.example/model.bin", "https://second.example/model.bin"]);
        var handler = new DisconnectingMirrorHandler(content, firstChunkLength: 11);
        using var client = new HttpClient(handler);
        var downloader = new ResumableModelDownloader(client);

        await downloader.DownloadAsync(
            [file],
            _testDirectory,
            progress: null,
            CancellationToken.None);

        Assert.Equal(11, handler.SecondMirrorRangeStart);
        Assert.Equal(
            content,
            await File.ReadAllBytesAsync(Path.Combine(_testDirectory, file.FileName)));
    }

    private static DownloadableModelFile CreateDownloadableFile(byte[] content)
    {
        return new DownloadableModelFile(
            "测试模型",
            "model.bin",
            content.Length,
            Convert.ToHexString(SHA256.HashData(content)),
            ["https://models.example/model.bin"]);
    }

    private sealed class RejectingRequestHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            throw new InvalidOperationException(
                "完整断点文件不应再次请求服务器。");
        }
    }

    private sealed class RecordingDownloadHandler(byte[] content)
        : HttpMessageHandler
    {
        public string UserAgent { get; private set; } = string.Empty;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            UserAgent = request.Headers.UserAgent.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(content),
            });
        }
    }

    private sealed class MirrorDownloadHandler(byte[] content) : HttpMessageHandler
    {
        public List<string> RequestHosts { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var host = request.RequestUri!.Host;
            RequestHosts.Add(host);
            var responseContent = host == "first.example"
                ? Encoding.UTF8.GetBytes("corrupt! model")
                : content;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(responseContent),
            });
        }
    }

    private sealed class DisconnectingMirrorHandler(byte[] content, int firstChunkLength)
        : HttpMessageHandler
    {
        public long? SecondMirrorRangeStart { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri!.Host == "first.example")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(new DisconnectingReadStream(
                        content.AsMemory(0, firstChunkLength).ToArray())),
                });
            }

            SecondMirrorRangeStart = request.Headers.Range?.Ranges.Single().From;
            var start = (int)(SecondMirrorRangeStart ?? 0);
            var response = new HttpResponseMessage(
                start > 0 ? HttpStatusCode.PartialContent : HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(content[start..]),
            };
            if (start > 0)
            {
                response.Content.Headers.ContentRange = new ContentRangeHeaderValue(
                    start,
                    content.Length - 1,
                    content.Length);
            }

            return Task.FromResult(response);
        }
    }

    private sealed class DisconnectingReadStream(byte[] prefix) : Stream
    {
        private readonly MemoryStream _inner = new(prefix, writable: false);
        private bool _hasDisconnected;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (_inner.Position < _inner.Length)
            {
                return await _inner.ReadAsync(buffer, cancellationToken);
            }

            if (!_hasDisconnected)
            {
                _hasDisconnected = true;
                throw new IOException("Connection was interrupted.");
            }

            return 0;
        }

        public override void Flush() => throw new NotSupportedException();
        public override Task FlushAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, recursive: true);
        }
    }
}
