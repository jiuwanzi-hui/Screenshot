using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using Screenshot.App.Capture;
using Screenshot.App.Text;
using Xunit.Abstractions;

namespace Screenshot.App.Tests;

public sealed class MixedSelectionTranslationTests(ITestOutputHelper output)
{
    [TranslationModelFact]
    public async Task SuppliedMixedSelectionReachesOcrModelAndTranslatedOverlay()
    {
        using var stream = typeof(MixedSelectionTranslationTests).Assembly.GetManifestResourceStream(
            "Screenshot.App.Tests.Fixtures.mixed-ocr-host-selection.png")!;
        using var screenshot = new Bitmap(stream);
        using var image = new CapturedImage(screenshot.Clone(new Rectangle(446, 30, 190, 28), PixelFormat.Format32bppArgb));
        using var ocr = new HighQualityOcrModelManager(Environment.GetEnvironmentVariable("SNAPCUT_OCR_MODEL_DIRECTORY"));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var watch = Stopwatch.StartNew();
        var recognition = await HighQualityOcrService.RecognizeAsync(image, ocr, cancellation.Token);
        output.WriteLine($"OCR: {watch.ElapsedMilliseconds} ms; {recognition.Text}; {recognition.ErrorMessage}");
        Assert.True(recognition.IsSuccess, recognition.ErrorMessage);
        Assert.Contains("Host", recognition.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Core", recognition.Text, StringComparison.OrdinalIgnoreCase);
        var regions = TranslationPresentationLayout.GroupParagraphs(TranslationInkBounds.Refine(image.Bitmap,
            TranslationPresentationLayout.TightenToWordBounds(recognition.Regions, recognition.Words)));
        Assert.NotEmpty(regions);
        var input = regions.Select(region => region.Text).ToArray();
        Assert.False(input.All(text => TranslationTargetLanguageMatcher.IsAlreadyTargetLanguage(text, "zh-Hans")));
        var directory = Environment.GetEnvironmentVariable("SNAPCUT_TRANSLATION_MODEL_DIRECTORY")!;
        using var model = new LocalLargeTranslationModelManager(directory,
            lightweight: System.IO.Path.GetFileName(directory).Equals("Hy-MT2-1.8B", StringComparison.OrdinalIgnoreCase));
        var translator = new OrderedTranslationProvider([new LocalLargeModelTranslationProvider(model)]);
        for (var pass = 0; pass < 2; pass++)
        {
            watch.Restart();
            var result = await translator.TranslateSegmentsAsync(input, "auto", "zh-Hans", cancellation.Token);
            var text = string.Join(" ", result.Segments);
            output.WriteLine($"{model.DisplayName}, pass {pass + 1}: {watch.ElapsedMilliseconds} ms; {text}; {result.ErrorMessage}; {result.Notice}");
            Assert.True(result.IsSuccess, result.ErrorMessage);
            Assert.Empty(result.FailedSegmentIndices ?? []);
            Assert.Contains("核心", text);
            Assert.True(text.Contains("宿主", StringComparison.Ordinal) || text.Contains("主机", StringComparison.Ordinal), text);
            Assert.True(TranslationPresentationLayout.HasMeaningfulTranslation(string.Join(" ", input),
                TranslationPresentationLayout.NormalizeTranslatedText(string.Join(" ", input), text)));
        }
    }
}
