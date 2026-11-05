using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using Screenshot.App.Presentation;
using Screenshot.App.Text;
using Xunit.Abstractions;

namespace Screenshot.App.Tests;

public sealed class ModestHardwareTests(ITestOutputHelper output)
{
    [DesktopFact]
    public async Task ScrollPreviewBoundsCanBeReadOffDispatcherAndDisappearAfterClose()
    {
        Screenshot.App.Capture.ScrollCaptureProgressWindow? window = null;
        try
        {
            WpfTestHost.Invoke(() =>
            {
                window = new Screenshot.App.Capture.ScrollCaptureProgressWindow();
                window.Show();
            });
            Assert.NotNull(await Task.Run(() => window!.GetScreenRegion()));
            WpfTestHost.Invoke(() => window!.CloseFromCoordinator());
            Assert.Null(await Task.Run(() => window!.GetScreenRegion()));
        }
        finally { WpfTestHost.Invoke(() => window?.CloseFromCoordinator()); }
    }

    [JapaneseScreenshotFact]
    public async Task SuppliedJapaneseNewsCanBeRecognizedAndTranslatedOnCpu()
    {
        var modelDirectory = Environment.GetEnvironmentVariable("SNAPCUT_LIGHTWEIGHT_MODEL_DIRECTORY")!;
        using var bitmap = new System.Drawing.Bitmap(Environment.GetEnvironmentVariable("SNAPCUT_JAPANESE_NEWS_SCREENSHOT")!);
        // Original four-column news above the old translation popup, without
        // feeding the earlier popup's translated text back into OCR.
        using var image = new Screenshot.App.Capture.CapturedImage(bitmap.Clone(
            new System.Drawing.Rectangle(0, 0, 940, 330), System.Drawing.Imaging.PixelFormat.Format32bppArgb));
        using var ocr = new HighQualityOcrModelManager(Path.Combine(Path.GetDirectoryName(modelDirectory)!, Screenshot.App.Core.AppMetadata.HighQualityOcrModelDirectoryName));
        var timer = Stopwatch.StartNew();
        var recognition = await HighQualityOcrService.RecognizeAsync(image, ocr);
        Assert.True(recognition.IsSuccess, recognition.ErrorMessage);
        output.WriteLine($"Raw OCR: {JsonSerializer.Serialize(recognition.Regions)}");
        output.WriteLine($"OCR words: {JsonSerializer.Serialize(recognition.Words)}");
        var source = TranslationPresentationLayout.GroupParagraphs(
            TranslationPresentationLayout.TightenToWordBounds(recognition.Regions, recognition.Words))
            .Select(region => region.Text).ToArray();
        output.WriteLine($"OCR {timer.Elapsed.TotalSeconds:0.0}s: {JsonSerializer.Serialize(source)}");
        Assert.Contains("ラフティング", string.Join("\n", source));
        Assert.Contains("カフェ", string.Join("\n", source));
        Assert.DoesNotContain(source, text => text.Contains("古い街", StringComparison.Ordinal) &&
                                             text.Contains("ラフティング", StringComparison.Ordinal));
        Assert.DoesNotContain(source, text => text.Contains("カフェ", StringComparison.Ordinal) &&
                                             text.Contains("貴州", StringComparison.Ordinal));
        using var model = new LocalLargeTranslationModelManager(modelDirectory, lightweight: true);
        var translated = await new OrderedTranslationProvider([new LocalLargeModelTranslationProvider(model)])
            .TranslateSegmentsAsync(source, "auto", "zh-Hans");
        output.WriteLine($"Total {timer.Elapsed.TotalSeconds:0.0}s: {JsonSerializer.Serialize(translated)}");
        Assert.True(translated.IsSuccess, translated.ErrorMessage);
        Assert.Empty(translated.FailedSegmentIndices ?? []);
    }

    [Fact]
    public void WindowShellReflowsDuringResizingWithoutStretchingOrCachingTheForm()
    {
        WpfTestHost.Invoke(() =>
        {
            var child = new MeasuredForm();
            var owner = new Window { Content = child };
            using var attachment = WindowShell.Attach(owner);
            var shell = Assert.IsType<System.Windows.Controls.Border>(owner.Content);
            shell.Measure(new Size(980, 720));
            shell.Arrange(new Rect(0, 0, 980, 720));
            var count = child.MeasureCount;
            for (var width = 970; width >= 480; width -= 10)
            {
                shell.Measure(new Size(width, 500));
                shell.Arrange(new Rect(0, 0, width, 500));
                Assert.Equal(width - shell.BorderThickness.Left - shell.BorderThickness.Right, child.RenderSize.Width);
                Assert.True(child.RenderTransform.Value.IsIdentity);
                Assert.Null(child.CacheMode);
            }
            Assert.True(child.MeasureCount > count);
            count = child.MeasureCount;
            shell.Measure(new Size(480, 500));
            shell.Arrange(new Rect(0, 0, 480, 500));
            Assert.Equal(count, child.MeasureCount); // Unchanged viewport reuses WPF's layout.
            owner.Close();
        });
    }

    [Fact]
    public async Task LightweightChoiceUsesAnIndependentInstallationAndDownloadRequirement()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        using var full = new LocalLargeTranslationModelManager(path);
        using var light = new LocalLargeTranslationModelManager(path + "-light", lightweight: true);
        Assert.NotEqual(full.ModelPath, light.ModelPath);
        Assert.True(light.ModelDownloadSize < full.ModelDownloadSize / 2);
        var result = await new LocalLargeModelTranslationProvider(light).TranslateAsync("こんにちは", "ja", "zh-Hans");
        Assert.False(result.IsSuccess);
        Assert.Contains("Hy-MT2 1.8B", result.ErrorMessage);
        Assert.Contains("下载", result.ErrorMessage);
        Assert.Equal(0, await light.CleanupLegacyModelAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("OK", false)]
    [InlineData("{\"translations\":[\"会议在9:30开始。\",\"请保存这份文档。\"]}", true)]
    public async Task ConnectionTestRequiresRealCompleteTranslation(string reply, bool valid)
    {
        using var client = new HttpClient(new ReplyHandler(reply));
        var result = await TranslationModelCatalogService.TestAsync("https://example.test/v1", "custom-model", "test-key", client);
        Assert.Equal(valid, result.IsSuccess);
    }

    [LightweightModelFact]
    public async Task LightweightCpuWorkerTranslatesJapaneseAndCanBeCancelledAndRestarted()
    {
        using var manager = new LocalLargeTranslationModelManager(
            Environment.GetEnvironmentVariable("SNAPCUT_LIGHTWEIGHT_MODEL_DIRECTORY"), lightweight: true);
        var provider = new OrderedTranslationProvider([new LocalLargeModelTranslationProvider(manager)]);
        string[] text = ["関連文章", "古い街並みに新たな活力注ぐカフェなどの新たな業態　雲南省昆明",
            "第20回アジア競技大会の中国代表団は計1274人からなり、そのうち選手は815人です。",
            "ラフティングで暑さを吹き飛ばす観光客　貴州省"];
        var watch = Stopwatch.StartNew();
        var result = await provider.TranslateSegmentsAsync(text, "ja", "zh-Hans");
        output.WriteLine($"Cold CPU translation: {watch.Elapsed.TotalSeconds:0.0}s; {JsonSerializer.Serialize(result)}");
        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Empty(result.FailedSegmentIndices ?? []);
        Assert.Null(result.ErrorMessage);
        Assert.Contains("1274", result.Segments[2]);
        Assert.Contains("815", result.Segments[2]);
        Assert.Contains("Hy-MT2", result.Notice);
        using (var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(150)))
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.TranslateAsync(
                string.Concat(Enumerable.Repeat(text[2], 20)), "ja", "zh-Hans", cancel.Token));
        var restarted = await provider.TranslateAsync("Please save this document.", "en", "zh-Hans");
        output.WriteLine($"Restart: {restarted.Text}; {restarted.ErrorMessage}");
        Assert.True(restarted.IsSuccess, restarted.ErrorMessage);
    }

    private sealed class MeasuredForm : FrameworkElement
    {
        internal int MeasureCount { get; private set; }
        protected override Size MeasureOverride(Size availableSize) { MeasureCount++; return availableSize; }
    }
    private sealed class ReplyHandler(string reply) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var body = await request.Content!.ReadAsStringAsync(token);
            var content = reply.StartsWith("{\"translations\"", StringComparison.Ordinal)
                ? body.Contains("9:30", StringComparison.Ordinal) ? "会议在9:30开始。" : "请保存这份文档。"
                : reply;
            return new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { message = new { content } } } })) };
        }
    }
}

public sealed class LightweightModelFactAttribute : FactAttribute
{
    public LightweightModelFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SNAPCUT_LIGHTWEIGHT_MODEL_DIRECTORY")))
            Skip = "Set SNAPCUT_LIGHTWEIGHT_MODEL_DIRECTORY to run native CPU inference.";
    }
}

public sealed class JapaneseScreenshotFactAttribute : FactAttribute
{
    public JapaneseScreenshotFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SNAPCUT_LIGHTWEIGHT_MODEL_DIRECTORY")) ||
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SNAPCUT_JAPANESE_NEWS_SCREENSHOT")))
            Skip = "Requires the supplied Japanese news screenshot and installed OCR/translation models.";
    }
}
