using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Screenshot.App.Capture;
using Screenshot.App.Text;
using Screenshot.App.Editor;
using System.Windows.Media.Imaging;

namespace Screenshot.App.Tests;

public sealed class SmallOcrLayoutPerformanceTests
{
    [TranslationModelFact]
    public async Task TranslatesAndRendersTheOriginalSmallOcrPage()
    {
        using var image = new CapturedImage(new System.Drawing.Bitmap(Environment.GetEnvironmentVariable("SNAPCUT_WEB_TRANSLATION_IMAGE")!));
        using var ocr = new HighQualityOcrModelManager(Environment.GetEnvironmentVariable("SNAPCUT_OCR_MODEL_DIRECTORY"));
        using var manager = new LocalLargeTranslationModelManager(Environment.GetEnvironmentVariable("SNAPCUT_TRANSLATION_MODEL_DIRECTORY"), lightweight: true);
        var timer = Stopwatch.StartNew();
        var preparation = Environment.GetEnvironmentVariable("SNAPCUT_TRANSLATION_OVERLAP") == "1"
            ? manager.InferenceSession.PrepareDuringRecognitionAsync(Guid.NewGuid(), CancellationToken.None)
            : Task.CompletedTask;
        var recognition = await HighQualityOcrService.RecognizeAsync(image, ocr);
        Assert.True(recognition.IsSuccess, recognition.ErrorMessage);
        var regions = TranslationPresentationLayout.GroupParagraphs(TranslationInkBounds.Refine(image.Bitmap,
            TranslationPresentationLayout.TightenToWordBounds(recognition.Regions, recognition.Words)));
        var ocrSeconds = timer.Elapsed.TotalSeconds;
        using var backup = new OfflineTranslationModelManager(System.IO.Path.GetDirectoryName(manager.InstallationDirectory));
        var result = await new OrderedTranslationProvider([new LocalLargeModelTranslationProvider(manager, new OfflineTranslationProvider(backup))])
            .TranslateSegmentsAsync(regions.Select(r => r.Text).ToArray(), "auto", "zh-Hans");
        var translationCompleteSeconds = timer.Elapsed.TotalSeconds;
        await preparation;
        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Empty(result.FailedSegmentIndices ?? []);
        var report = Environment.GetEnvironmentVariable("SNAPCUT_WEB_TRANSLATION_REPORT")!;
        WpfTestHost.Invoke(() =>
        {
            var annotations = regions.Select((r, i) => new TranslatedTextAnnotationRegion(
                new System.Windows.Rect(Math.Max(0, r.X - 3), Math.Max(0, r.Y - 2), r.Width + 6, r.Height + 4),
                result.Segments[i], r.EstimatedFontSize)).ToArray();
            var canvas = new ImageEditorCanvas();
            canvas.Initialize(image, image.Bitmap.Width, image.Bitmap.Height);
            canvas.AddTranslationOverlay(annotations);
            var document = (EditorDocument)typeof(ImageEditorCanvas).GetField("_document", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(canvas)!;
            var actual = Assert.IsType<TranslationOverlayAnnotation>(Assert.Single(document.Annotations)).Regions;
            Assert.Equal(regions.Count, actual.Count);
            Assert.All(actual, item => Assert.True(TranslationTextLayout.LayoutParagraph(item).FontSize >= 16));
            var layout = actual.Select(TranslationTextLayout.LayoutParagraph).ToArray();
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(canvas.RenderEditedImage()));
            using var output = File.Create(Path.ChangeExtension(report, ".png"));
            encoder.Save(output);
            File.WriteAllText(report, JsonSerializer.Serialize(new { ocrSeconds, translationCompleteSeconds,
                totalWithOffscreenExportSeconds = timer.Elapsed.TotalSeconds, regions, result, actual, layout }, new JsonSerializerOptions { WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        });
    }

    [OcrPerformanceFact]
    public async Task MeasuresOriginalScreenshotAndLayoutStages()
    {
        using var manager = new HighQualityOcrModelManager(Environment.GetEnvironmentVariable("SNAPCUT_OCR_MODEL_DIRECTORY"));
        using var original = new System.Drawing.Bitmap(Environment.GetEnvironmentVariable("SNAPCUT_OCR_IMAGES")!);
        var crop = Environment.GetEnvironmentVariable("SNAPCUT_OCR_CROP")!.Split(',').Select(int.Parse).ToArray();
        using var image = new CapturedImage(original.Clone(new(crop[0], crop[1], crop[2], crop[3]), System.Drawing.Imaging.PixelFormat.Format32bppArgb));
        image.Bitmap.Save(Path.ChangeExtension(Environment.GetEnvironmentVariable("SNAPCUT_OCR_REPORT")!, ".source.png"));
        var reports = new List<object>();
        for (var run = 0; run < 3; run++)
        {
            var timer = Stopwatch.StartNew();
            var result = await HighQualityOcrService.RecognizeAsync(image, manager);
            var ocrSeconds = timer.Elapsed.TotalSeconds;
            timer.Restart();
            var tightened = TranslationPresentationLayout.TightenToWordBounds(result.Regions, result.Words);
            var refined = TranslationInkBounds.Refine(image.Bitmap, tightened);
            var paragraphs = TranslationPresentationLayout.GroupParagraphs(refined);
            var worker = (Process)typeof(OcrWorkerProcess).GetField("_process", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null)!;
            worker.Refresh();
            reports.Add(new { run, ocrSeconds, layoutSeconds = timer.Elapsed.TotalSeconds, result, tightened, refined, paragraphs,
                workingSetMiB = worker.WorkingSet64 / 1048576d, peakWorkingSetMiB = worker.PeakWorkingSet64 / 1048576d,
                privateCommitMiB = worker.PrivateMemorySize64 / 1048576d });
            if (Environment.GetEnvironmentVariable("SNAPCUT_OCR_BASELINE") is { } baseline)
            {
                using var prior = JsonDocument.Parse(File.ReadAllText(baseline));
                var expected = prior.RootElement[0].GetProperty("result").Deserialize<OcrRecognitionResult>()!;
                Assert.Equal(expected.Text, result.Text);
                Assert.Equal(expected.Regions, result.Regions);
                Assert.Equal(expected.Words, result.Words);
            }
            await File.WriteAllTextAsync(Environment.GetEnvironmentVariable("SNAPCUT_OCR_REPORT")!, JsonSerializer.Serialize(reports,
                new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            Assert.True(result.IsSuccess, result.ErrorMessage);
            Assert.Contains("Windows", result.Text);
        }
    }
}
