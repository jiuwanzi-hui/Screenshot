using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text.Json;
using Screenshot.App.Capture;
using Screenshot.App.Text;

namespace Screenshot.App.Tests;

public sealed class TechnicalParagraphTranslationTests
{
    private const string Source = @"Extract the portable archive and run SnapCut.exe; neither installation nor a preinstalled .NET or Visual C++ runtime is required. Both packages store their data in ScreenshotData under their respective application directories, so back up that directory before moving or uninstalling the application. Version 2.8.3 retains the existing data layout and attempts to migrate legacy 1.0.0 %LocalAppData%\Screenshot data when found, removing the old directory after a successful migration.";
    private static readonly JsonSerializerOptions ReportOptions = new()
        { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    [LocalRuntimeTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RecognizesAndTranslatesTechnicalParagraphWithoutDroppingVersions(bool lightweight)
    {
        var root = Environment.GetEnvironmentVariable("SNAPCUT_TRANSLATION_MODEL_ROOT")!;
        using var manager = new LocalLargeTranslationModelManager(Path.Combine(root,
            lightweight ? "Hy-MT2-1.8B" : "TranslateGemma-4B-Q4-K-M"), lightweight: lightweight);
        using var ocr = new HighQualityOcrModelManager(Path.Combine(root, Screenshot.App.Core.AppMetadata.HighQualityOcrModelDirectoryName));
        using var original = new Bitmap(Environment.GetEnvironmentVariable("SNAPCUT_TECHNICAL_PARAGRAPH_IMAGE")!);
        using var image = new CapturedImage(original.Clone(new Rectangle(82, 156, 1100, 660), System.Drawing.Imaging.PixelFormat.Format32bppArgb));
        var recognized = await HighQualityOcrService.RecognizeAsync(image, ocr);
        Assert.True(recognized.IsSuccess, recognized.ErrorMessage);
        var paragraphs = TranslationPresentationLayout.GroupParagraphs(TranslationInkBounds.Refine(image.Bitmap,
            TranslationPresentationLayout.TightenToWordBounds(recognized.Regions, recognized.Words)));
        var recognizedSource = string.Join(" ", paragraphs.Where(p => p.Y < 155).Select(p => p.Text));
        Assert.Contains("Extract the portable archive", recognizedSource);
        Assert.Contains("1.0.0", recognizedSource);
        Assert.Contains(@"%LocalAppData%\Screenshot", recognizedSource);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var reports = new List<object>();
        // Check the original text and actual OCR, then a warm repeat. Adjacent
        // translated paragraphs must remain untouched.
        foreach (var source in new[] { Source, recognizedSource, recognizedSource })
        {
            const string alreadyTranslated = "版本 3.8.1 改进了固定图片缩放功能。";
            var timer = Stopwatch.StartNew();
            var result = await new LocalLargeModelTranslationProvider(manager).TranslateSegmentsAsync(
                [source, alreadyTranslated], "auto", "zh-Hans", deadline.Token);
            reports.Add(new { source, seconds = timer.Elapsed.TotalSeconds, result });
            await File.WriteAllTextAsync(Path.Combine(Environment.GetEnvironmentVariable("SNAPCUT_TECHNICAL_PARAGRAPH_REPORT")!,
                lightweight ? "verified-hy.json" : "verified-gemma.json"), JsonSerializer.Serialize(new { recognized, paragraphs, reports }, ReportOptions));
            Assert.True(result.IsSuccess, result.ErrorMessage);
            Assert.Empty(result.FailedSegmentIndices ?? []);
            Assert.Equal(alreadyTranslated, result.Segments[1]);
            var translated = result.Segments[0];
            Assert.Null(TranslationCompletenessValidator.Validate(source, translated, "zh"));
            Assert.Contains("2.8.3", translated);
            Assert.Contains("1.0.0", translated);
            Assert.Contains(@"%LocalAppData%\Screenshot", translated);
            Assert.Contains("ScreenshotData", translated);
            Assert.Contains("安装", translated);
            Assert.Contains("备份", translated);
            Assert.Contains("迁移", translated);
            Assert.Contains("删除", translated);
            Assert.DoesNotContain("ZXQKEEP", translated);
            Assert.DoesNotContain("已自动补译", result.Notice);
        }
    }
}
