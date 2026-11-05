using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Screenshot.App.Core;
using Screenshot.App.Text;

namespace Screenshot.App.Tests;

public sealed class TranslationTargetRoutingTests
{
    private static readonly JsonSerializerOptions ReportOptions = new() { WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    internal static readonly string[] EnglishPage =
    [
        "We know knowledge",
        "Helping communities of people and AI get trusted knowledge since 2008. We're building the products that put it to work.",
        "See Stack Internal",
    ];

    [Fact]
    public async Task EnglishPageReturnsUnchangedWithoutStartingAnyProvider()
    {
        var translator = new RecordingProvider();
        var result = await new OrderedTranslationProvider([translator]).TranslateSegmentsAsync(EnglishPage, "auto", "en");
        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Equal(EnglishPage, result.Segments);
        Assert.Empty(translator.Requests);
        Assert.Contains("未调用翻译模型", result.Notice);
    }

    [Theory]
    [InlineData("La délégation compte 1274 personnes.")]
    [InlineData("La delegación tiene 1274 miembros.")]
    [InlineData("Bonjour")]
    [InlineData("知识让我们的工作更高效。")]
    [InlineData("設定を開いてください。")]
    public void TargetContextDoesNotHideOtherLanguages(string other)
    {
        string[] input = [.. EnglishPage, other];
        Assert.Equal([3], TranslationTargetLanguageMatcher.GetPendingSegmentIndices(input, "auto", "en"));
    }

    [Fact]
    public async Task MixedPageOnlyTranslatesForeignSegmentsAndKeepsTheirOriginalPositions()
    {
        var translator = new RecordingProvider();
        string[] input = [EnglishPage[1], "请打开设置。", "1274", "请保存文件。"];
        var result = await new OrderedTranslationProvider([translator]).TranslateSegmentsAsync(input, "auto", "en");
        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Equal(["请打开设置。", "请保存文件。"], Assert.Single(translator.Requests));
        Assert.Equal([input[0], "Please open the settings.", "1274", "Please save the file."], result.Segments);
        Assert.Empty(result.FailedSegmentIndices ?? []);
    }

    [Fact]
    public async Task MixedPageFailureRetainsOriginalsAndReportsOnlyPendingIndices()
    {
        var translator = new RecordingProvider { Fail = true };
        string[] input = [EnglishPage[1], "请打开设置。", "1274", "请保存文件。"];
        var result = await new OrderedTranslationProvider([translator]).TranslateSegmentsAsync(input, "auto", "en");
        Assert.Equal(input, result.Segments);
        Assert.Equal([1, 3], result.FailedSegmentIndices);
        Assert.Contains("offline unavailable", result.ErrorMessage);
    }

    [Fact]
    public void ExplicitDifferentSourceAndJapaneseHeadingsStillRequireTranslation()
    {
        Assert.Equal([0, 1, 2], TranslationTargetLanguageMatcher.GetPendingSegmentIndices(EnglishPage, "de", "en"));
        Assert.Equal([0, 1], TranslationTargetLanguageMatcher.GetPendingSegmentIndices(["設定", "設定を開いてください。"], "auto", "zh-Hans"));
    }

    private sealed class RecordingProvider : ITranslationProvider
    {
        public string Id => "test-offline";
        internal List<string[]> Requests { get; } = [];
        internal bool Fail { get; init; }
        public Task<TranslationResult> TranslateAsync(string text, string sourceLanguage, string targetLanguage,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException("Use segments.");
        public Task<TranslationSegmentsResult> TranslateSegmentsAsync(IReadOnlyList<string> segments, string sourceLanguage,
            string targetLanguage, CancellationToken cancellationToken = default)
        {
            Requests.Add(segments.ToArray());
            return Task.FromResult(Fail ? TranslationSegmentsResult.Failure("offline unavailable") :
                new TranslationSegmentsResult(true, segments.Select(text => text == "请打开设置。" ?
                    "Please open the settings." : "Please save the file.").ToArray(), null));
        }
    }

    [ScreenshotTranslationPerformanceFact]
    public async Task MeasuresEnglishTargetWithInstalledModel()
    {
        var directory = Environment.GetEnvironmentVariable("SNAPCUT_PERFORMANCE_BUILD")!;
        var reportPath = Environment.GetEnvironmentVariable("SNAPCUT_PERFORMANCE_REPORT")!;
        using var manager = new LocalLargeTranslationModelManager(Path.Combine(directory, "TranslationModels",
            AppMetadata.LocalLargeTranslationModelDirectoryName));
        var provider = new OrderedTranslationProvider([new LocalLargeModelTranslationProvider(manager)]);
        var reports = new List<object>();
        if (Environment.GetEnvironmentVariable("SNAPCUT_TARGET_IMAGE") is { Length: > 0 } imagePath)
        {
            using var sourceImage = new System.Drawing.Bitmap(imagePath);
            using var image = new Screenshot.App.Capture.CapturedImage(sourceImage.Clone(
                new System.Drawing.Rectangle(28, 75, 674, 474), System.Drawing.Imaging.PixelFormat.Format32bppArgb));
            using var ocr = new HighQualityOcrModelManager(Path.Combine(directory, "TranslationModels", Screenshot.App.Core.AppMetadata.HighQualityOcrModelDirectoryName));
            var watch = Stopwatch.StartNew();
            var recognition = await HighQualityOcrService.RecognizeAsync(image, ocr);
            Assert.True(recognition.IsSuccess, recognition.ErrorMessage);
            var regions = TranslationPresentationLayout.GroupParagraphs(TranslationInkBounds.Refine(image.Bitmap,
                TranslationPresentationLayout.TightenToWordBounds(recognition.Regions, recognition.Words)));
            var ocrSeconds = watch.Elapsed.TotalSeconds;
            watch.Restart();
            var result = await provider.TranslateSegmentsAsync(regions.Select(region => region.Text).ToArray(), "auto", "en");
            reports.Add(new { name = "supplied-image-English", ocrSeconds, seconds = watch.Elapsed.TotalSeconds,
                input = regions.Select(region => region.Text), result.IsSuccess, result.ErrorMessage, result.Notice, result.FailedSegmentIndices });
            await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(reports, ReportOptions));
            Assert.True(result.IsSuccess, result.ErrorMessage);
            Assert.Empty(result.FailedSegmentIndices ?? []);
        }
        string[] chinese = ["我们掌握知识", "自2008年起，我们致力于帮助社区和个人获取可靠的知识。我们正在开发产品，将这些知识转化为实际应用。", "查看内部文档"];
        foreach (var (name, input) in new[] { ("already-English", EnglishPage), ("Chinese-to-English-cold", chinese), ("Chinese-to-English-warm", chinese) })
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(75));
            var watch = Stopwatch.StartNew();
            var progress = new System.Collections.Concurrent.ConcurrentQueue<string>();
            using var progressScope = TranslationProgress.Begin(progress.Enqueue);
            var result = await provider.TranslateSegmentsAsync(input, "auto", "en", cancellation.Token);
            var elapsedSeconds = watch.Elapsed.TotalSeconds;
            using var worker = Process.GetProcessesByName("llama-server").FirstOrDefault(process =>
                process.MainModule?.FileName.StartsWith(manager.InstallationDirectory, StringComparison.OrdinalIgnoreCase) == true);
            reports.Add(new { name, seconds = elapsedSeconds, model = manager.DisplayName,
                workerWorkingSetBytes = worker?.WorkingSet64, workerPeakWorkingSetBytes = worker?.PeakWorkingSet64,
                progress = progress.ToArray(),
                detection = input.Select(text => Cld3OfflineLanguageDetector.Shared.Detect(text)),
                result.IsSuccess, result.ErrorMessage, result.Notice, result.FailedSegmentIndices, result.Segments });
            await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(reports, ReportOptions));
            Assert.True(result.IsSuccess, result.ErrorMessage);
            Assert.Empty(result.FailedSegmentIndices ?? []);
        }
    }
}
