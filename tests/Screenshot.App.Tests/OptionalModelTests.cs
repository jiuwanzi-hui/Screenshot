using System.IO;
using System.Net;
using System.Net.Http;
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

    [Fact]
    public void OfflineTranslationConfigurationUsesTheSharedCpuBudget()
    {
        var configuration = OfflineTranslationModelCatalog.CreateConfiguration(
            "model.bin",
            "source.spm",
            "target.spm",
            null);

        Assert.Contains(
            $"cpu-threads: {HeavyWorkloadBudget.CpuThreadCount}",
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

    public void Dispose()
    {
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, recursive: true);
        }
    }
}
