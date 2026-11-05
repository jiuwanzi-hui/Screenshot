using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Screenshot.App.Capture;
using Screenshot.App.Text;
using Screenshot.App.Editor;
using System.Windows.Media.Imaging;

namespace Screenshot.App.Tests;

public sealed class WebPageTranslationTests
{
    private static readonly JsonSerializerOptions ReportOptions = new() { WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    [TranslationModelFact]
    public async Task SuppliedMulticolumnPageRetainsParagraphsAndTranslations()
    {
        var modelDirectory = Environment.GetEnvironmentVariable("SNAPCUT_TRANSLATION_MODEL_DIRECTORY")!;
        var path = Environment.GetEnvironmentVariable("SNAPCUT_WEB_TRANSLATION_IMAGE")!;
        using var bitmap = new System.Drawing.Bitmap(path);
        using var image = new CapturedImage((System.Drawing.Bitmap)bitmap.Clone());
        using var ocr = new HighQualityOcrModelManager(Path.Combine(Path.GetDirectoryName(modelDirectory)!, Screenshot.App.Core.AppMetadata.HighQualityOcrModelDirectoryName));
        var timer = Stopwatch.StartNew();
        var recognition = await HighQualityOcrService.RecognizeAsync(image, ocr);
        Assert.True(recognition.IsSuccess, recognition.ErrorMessage);
        var regions = TranslationPresentationLayout.GroupParagraphs(
            TranslationInkBounds.Refine(image.Bitmap,
                TranslationPresentationLayout.TightenToWordBounds(recognition.Regions, recognition.Words)));
        var ocrSeconds = timer.Elapsed.TotalSeconds;
        using var manager = new LocalLargeTranslationModelManager(modelDirectory);
        using var backupManager = new OfflineTranslationModelManager(Path.GetDirectoryName(modelDirectory));
        var result = await new OrderedTranslationProvider([new LocalLargeModelTranslationProvider(manager,
            new OfflineTranslationProvider(backupManager))])
            .TranslateSegmentsAsync(regions.Select(region => region.Text).ToArray(), "auto", "zh-Hans");
        var report = Environment.GetEnvironmentVariable("SNAPCUT_WEB_TRANSLATION_REPORT");
        if (!string.IsNullOrWhiteSpace(report))
            await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new
            {
                ocrSeconds, totalSeconds = timer.Elapsed.TotalSeconds, recognition, regions, result,
            }, ReportOptions));
        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Equal(regions.Count, result.Segments.Count);
        Assert.Null(result.ErrorMessage);
        WpfTestHost.Invoke(() =>
        {
            var annotations = regions.Select((region, index) => (region, text: result.Segments[index]))
                .Where(item => TranslationPresentationLayout.HasMeaningfulTranslation(item.region.Text, item.text))
                .Select(item => new TranslatedTextAnnotationRegion(
                    new System.Windows.Rect(Math.Max(0, item.region.X - 3), Math.Max(0, item.region.Y - 2),
                        Math.Max(20, item.region.Width + 6), Math.Max(12, item.region.Height + 4)),
                    item.text, Math.Clamp(item.region.EstimatedFontSize, 8, 32))).ToArray();
            foreach (var annotation in annotations)
            {
                var layout = TranslationTextLayout.LayoutParagraph(annotation);
                Assert.All(layout.Lines, line => Assert.True(line.Y + line.Height <= annotation.Bounds.Bottom + 0.5,
                    $"Translation would be clipped: {annotation.Text}"));
            }
            var canvas = new ImageEditorCanvas();
            canvas.Initialize(image, bitmap.Width, bitmap.Height);
            canvas.AddTranslationOverlay(annotations);
            if (!string.IsNullOrWhiteSpace(report))
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(canvas.RenderEditedImage()));
                using var file = File.Create(Path.ChangeExtension(report, ".png"));
                encoder.Save(file);
            }
        });
    }
}
