using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using Screenshot.App.Capture;
using Screenshot.App.Text;
using Xunit.Abstractions;

namespace Screenshot.App.Tests;

public sealed class SmallSelectionOcrTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("月翼形态\r\n123", "月翼形态 Lunarwing 123", true)]
    [InlineData("Lunarwing Form", "Lunarwing Form 123", true)]
    [InlineData("Lunarwing Form", "Lunarwing From 123", false)]
    [InlineData("123", "123", false)]
    [InlineData("123", "", false)]
    public void SmallTextRetryMustPreserveExistingCharacters(string original, string retry, bool expected)
    {
        Assert.Equal(expected, HighQualityOcrService.IsCompleteSmallTextExpansion(original, retry));
    }

    private sealed class OcrWorkerFactAttribute : FactAttribute
    {
        public OcrWorkerFactAttribute()
        {
            if (!System.IO.Directory.Exists(Environment.GetEnvironmentVariable("SNAPCUT_OCR_MODEL_DIRECTORY")))
                Skip = "Set SNAPCUT_OCR_MODEL_DIRECTORY to run local OCR regressions without desktop capture.";
        }
    }

    [OcrWorkerFact]
    public async Task RealYellowTitleSurvivesSelectionMarginsAndEditorExport()
    {
        using var stream = typeof(SmallSelectionOcrTests).Assembly.GetManifestResourceStream(
            "Screenshot.App.Tests.Fixtures.small-yellow-title.png")!;
        using var source = new Bitmap(stream);
        using var manager = new HighQualityOcrModelManager(Environment.GetEnvironmentVariable("SNAPCUT_OCR_MODEL_DIRECTORY"));
        foreach (var margin in new[] { 0, 2, 8, 16 })
        {
            using var image = new CapturedImage(new Bitmap(source.Width + margin * 2, source.Height + margin * 2));
            using (var graphics = Graphics.FromImage(image.Bitmap))
            {
                graphics.Clear(source.GetPixel(0, 0));
            }
            // Avoid GDI DPI conversion of the supplied PNG when adding margins.
            for (var y = 0; y < source.Height; y++)
            for (var x = 0; x < source.Width; x++)
                image.Bitmap.SetPixel(x + margin, y + margin, source.GetPixel(x, y));
            var watch = Stopwatch.StartNew();
            var direct = await HighQualityOcrService.RecognizeAsync(image, manager);
            output.WriteLine($"margin={margin}: {watch.ElapsedMilliseconds} ms, {direct.Text}");
            AssertTitleAndCoordinates(direct, image, margin);
            CapturedImage? rendered = null;
            WpfTestHost.Invoke(() =>
            {
                var canvas = new Screenshot.App.Editor.ImageEditorCanvas();
                canvas.Initialize(image, image.Bitmap.Width / 1.5, image.Bitmap.Height / 1.5);
                rendered = CapturedImage.FromBitmapSource(canvas.RenderEditedImage());
                canvas.Reset();
            });
            using (rendered)
            {
                var exported = await HighQualityOcrService.RecognizeAsync(rendered!, manager);
                AssertTitleAndCoordinates(exported, image, margin);
                Assert.Equal(direct.Text, exported.Text);
            }
        }
    }

    [OcrWorkerFact]
    public async Task SmallColoredLabelsAndBlankAreasDoNotRegress()
    {
        using var manager = new HighQualityOcrModelManager(Environment.GetEnvironmentVariable("SNAPCUT_OCR_MODEL_DIRECTORY"));
        foreach (var (foreground, background) in new[] {
            (Color.Gold, Color.FromArgb(32, 32, 32)), (Color.White, Color.Black),
            (Color.Black, Color.White), (Color.DarkBlue, Color.White),
            (Color.Cyan, Color.FromArgb(32, 32, 32)) })
        {
            using var image = new CapturedImage(new Bitmap(300, 55, PixelFormat.Format32bppArgb));
            using (var graphics = Graphics.FromImage(image.Bitmap))
            using (var font = new Font("Segoe UI", 16, FontStyle.Regular, GraphicsUnit.Pixel))
            using (var brush = new SolidBrush(foreground))
            {
                graphics.Clear(background);
                graphics.DrawString("Lunarwing Form 123", font, brush, 3, 12);
            }
            var result = await HighQualityOcrService.RecognizeAsync(image, manager);
            Assert.True(result.IsSuccess, result.ErrorMessage);
            Assert.Contains("Lunarwing", result.Text, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("123", result.Text);
            using (var graphics = Graphics.FromImage(image.Bitmap)) graphics.Clear(background);
            var blank = await HighQualityOcrService.RecognizeAsync(image, manager);
            Assert.True(blank.IsSuccess, blank.ErrorMessage);
            Assert.Empty(blank.Regions);
            Assert.Empty(blank.Text);
        }
    }

    [OcrWorkerFact]
    public async Task SmallChineseAndMixedLabelsRemainReadable()
    {
        using var manager = new HighQualityOcrModelManager(Environment.GetEnvironmentVariable("SNAPCUT_OCR_MODEL_DIRECTORY"));
        foreach (var text in new[] { "大德鲁伊的月翼形态", "月翼形态 Lunarwing 123" })
        {
            using var image = new CapturedImage(new Bitmap(380, 65));
            using (var graphics = Graphics.FromImage(image.Bitmap))
            using (var font = new Font("Microsoft YaHei UI", 18, FontStyle.Regular, GraphicsUnit.Pixel))
            {
                graphics.Clear(Color.FromArgb(32, 32, 32));
                graphics.DrawString(text, font, Brushes.Gold, 6, 18);
            }
            var result = await HighQualityOcrService.RecognizeAsync(image, manager);
            Assert.True(result.IsSuccess, result.ErrorMessage);
            Assert.Equal(text.Replace(" ", ""), result.Text.Replace(" ", ""));
        }
    }

    private static void AssertTitleAndCoordinates(OcrRecognitionResult result, CapturedImage image, int margin)
    {
        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Equal("Archdruid's Lunarwing Form", result.Text.Trim());
        var region = Assert.Single(result.Regions);
        // Original fixture ink is x~10..243, y~22..39, not 2x coordinates.
        Assert.InRange(region.X, margin, margin + 22);
        Assert.InRange(region.Y, margin + 12, margin + 32);
        Assert.InRange(region.Width, 215, 255);
        Assert.InRange(region.Height, 10, 35);
        Assert.InRange(region.EstimatedFontSize, 8, 32);
        Assert.NotEmpty(result.Words);
        Assert.All(result.Words, word =>
        {
            Assert.InRange(word.X, 0, image.Bitmap.Width);
            Assert.InRange(word.Y, 0, image.Bitmap.Height);
            Assert.InRange(word.X + word.Width, 1, image.Bitmap.Width);
            Assert.InRange(word.Y + word.Height, 1, image.Bitmap.Height);
        });
    }
}
