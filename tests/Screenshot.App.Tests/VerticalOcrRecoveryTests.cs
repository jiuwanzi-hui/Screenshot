using System.Drawing;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using RapidOcrNet;
using Screenshot.App.Capture;
using Screenshot.App.Text;
using SkiaSharp;

namespace Screenshot.App.Tests;

public sealed class VerticalOcrRecoveryTests
{
    [Fact]
    public void GeometryRejectsHorizontalSkewedAndOversizedHypotheses()
    {
        Assert.True(VerticalOcrRecovery.TryGetColumn(Box(6, 24, 85, 286), 235, 321, out var bounds, out var count));
        Assert.Equal(3, count);
        Assert.Equal(new SKRectI(6, 24, 91, 310), bounds);
        Assert.False(VerticalOcrRecovery.TryGetColumn(Box(5, 5, 250, 60), 600, 600, out _, out _));
        Assert.False(VerticalOcrRecovery.TryGetColumn(Box(0, 0, 350, 3000), 4000, 4000, out _, out _));
        Assert.False(VerticalOcrRecovery.TryGetColumn([new(0, 0), new(85, 0), new(185, 286), new(100, 286)], 600, 600, out _, out _));
    }

    [Fact]
    public void CharacterBoxesMapBackToTheirOriginalVerticalTiles()
    {
        for (var index = 0; index < 3; index++)
        {
            var points = Box(index * 85 + 8, 6, 65, 76);
            VerticalOcrRecovery.MapCharacter(points, new(6, 24, 91, 310), 3, 95);
            Assert.All(points, p => { Assert.InRange(p.X, 6, 91); Assert.InRange(p.Y, 24, 310); });
            Assert.InRange(points[0].Y, 24 + index * 95, 34 + index * 96);
            Assert.Equal(14, points[0].X);
        }
    }

    [Theory]
    [InlineData("鼠虎猴", .95f, .35f, true)]
    [InlineData("ABC", .95f, .35f, false)]
    [InlineData("鼠虎", .95f, .35f, false)]
    [InlineData("鼠虎猴", .7f, .35f, false)]
    [InlineData("鼠虎猴", .95f, .9f, false)]
    [InlineData("鼠虎猴", .998f, .851f, true)]
    [InlineData("鼠虎猴", .97f, .851f, false)]
    public void RecoveryRequiresCompleteCjkAndAConfidenceImprovement(string text, float score, float originalScore, bool expected)
    {
        var original = new TextBlock { Text = "房闲", CharScores = [originalScore], Chars = [], BoxPoints = Box(0, 0, 50, 150) };
        var candidate = new TextBlock { Text = text, CharScores = [score], Chars = [], BoxPoints = Box(0, 0, 150, 50),
            WordResults = Enumerable.Range(0, 3).Select(i => new WordBox { Text = "字", Score = score, BoxPoints = Box(i * 50, 0, 50, 50) }).ToArray() };
        Assert.Equal(expected, VerticalOcrRecovery.IsBetterColumn(original, candidate, 3));
    }

    [ArtisticSampleFact]
    public async Task RealArtisticColumnsUseOriginalPixelsAndKeepMappedCoordinates()
    {
        using var manager = new HighQualityOcrModelManager(Environment.GetEnvironmentVariable("SNAPCUT_OCR_MODEL_DIRECTORY"));
        using var source = new Bitmap(Environment.GetEnvironmentVariable("SNAPCUT_ARTISTIC_OCR_SAMPLE")!);
        using var image = new CapturedImage(new Bitmap(source));
        var timer = Stopwatch.StartNew();
        var result = await HighQualityOcrService.RecognizeAsync(image, manager);
        Console.WriteLine($"Artistic columns: {timer.ElapsedMilliseconds} ms; {JsonSerializer.Serialize(result.Text)}");
        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Contains("鼠虎猴", result.Text);
        Assert.Contains("牛兔", result.Text);
        Assert.DoesNotContain("房", result.Text);
        Assert.Equal(6, result.Words.Count);
        Assert.All(result.Words, word =>
        {
            Assert.InRange(word.X, 0, source.Width); Assert.InRange(word.Y, 0, source.Height);
            Assert.InRange(word.X + word.Width, 1, source.Width); Assert.InRange(word.Y + word.Height, 1, source.Height);
        });
        for (var y = 0; y < source.Height; y++)
            for (var x = 0; x < source.Width; x++)
                Assert.Equal(source.GetPixel(x, y), image.Bitmap.GetPixel(x, y));

        using var observer = Process.GetProcessById(Worker().Id);
        timer.Restart();
        var warm = await HighQualityOcrService.RecognizeAsync(image, manager);
        Console.WriteLine($"Artistic warm: {timer.ElapsedMilliseconds} ms");
        Assert.Equal(result.Text, warm.Text);
        Assert.Equal(result.Words, warm.Words);
        Assert.Equal(observer.Id, Worker().Id);
        observer.Refresh();
        Console.WriteLine($"Artistic worker: total working set={observer.WorkingSet64 / 1048576d:F1} MiB; " +
            $"peak total working set={observer.PeakWorkingSet64 / 1048576d:F1} MiB; private commit={observer.PrivateMemorySize64 / 1048576d:F1} MiB");
        using var idleTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await observer.WaitForExitAsync(idleTimeout.Token);
        timer.Restart();
        var reloaded = await HighQualityOcrService.RecognizeAsync(image, manager);
        Console.WriteLine($"Artistic reload: {timer.ElapsedMilliseconds} ms");
        Assert.NotEqual(observer.Id, Worker().Id);
        Assert.Equal(result.Text, reloaded.Text);
        Assert.Equal(result.Words, reloaded.Words);
        using var cancelledWorker = Process.GetProcessById(Worker().Id);
        using (var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(30)))
        {
            var cancelled = await HighQualityOcrService.RecognizeAsync(image, manager, cancel.Token);
            Assert.False(cancelled.IsSuccess);
            Assert.Contains("取消", cancelled.ErrorMessage);
        }
        using var cancelTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await cancelledWorker.WaitForExitAsync(cancelTimeout.Token);
        var recovered = await HighQualityOcrService.RecognizeAsync(image, manager);
        Assert.NotEqual(cancelledWorker.Id, Worker().Id);
        Assert.Equal(result.Text, recovered.Text);
        Assert.Equal(result.Words, recovered.Words);
        using var final = Process.GetProcessById(Worker().Id);
        using var finalTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await final.WaitForExitAsync(finalTimeout.Token);
    }

    private static SKPointI[] Box(int x, int y, int width, int height) =>
        [new(x, y), new(x + width, y), new(x + width, y + height), new(x, y + height)];

    [ArtisticSampleFact]
    public async Task ArtisticColumnsSurviveDisplayScalingAndSelectionMargins()
    {
        using var manager = new HighQualityOcrModelManager(Environment.GetEnvironmentVariable("SNAPCUT_OCR_MODEL_DIRECTORY"));
        using var source = new CapturedImage(new Bitmap(Environment.GetEnvironmentVariable("SNAPCUT_ARTISTIC_OCR_SAMPLE")!));
        var failures = new List<string>();
        foreach (var scale in new[] { 100, 125, 150, 175, 200 })
        {
            using var scaled = source.CreateScaled(scale);
            foreach (var margin in new[] { 0, 8 })
            {
                using var sample = new CapturedImage(new Bitmap(scaled.Bitmap.Width + margin * 2, scaled.Bitmap.Height + margin * 2));
                using (var graphics = Graphics.FromImage(sample.Bitmap))
                {
                    graphics.Clear(Color.Black);
                }
                // Pixel copy avoids GDI's source-DPI interpretation when a
                // supplied PNG has physical-resolution metadata.
                for (var y = 0; y < scaled.Bitmap.Height; y++)
                    for (var x = 0; x < scaled.Bitmap.Width; x++)
                        sample.Bitmap.SetPixel(x + margin, y + margin, scaled.Bitmap.GetPixel(x, y));
                var clock = Stopwatch.StartNew();
                var result = await HighQualityOcrService.RecognizeAsync(sample, manager);
                Console.WriteLine(JsonSerializer.Serialize(new { scale, margin, ms = clock.ElapsedMilliseconds, result.Text }));
                AssertMappedWords(result, sample);
                for (var y = 0; y < scaled.Bitmap.Height; y++)
                    for (var x = 0; x < scaled.Bitmap.Width; x++)
                        Assert.Equal(scaled.Bitmap.GetPixel(x, y), sample.Bitmap.GetPixel(x + margin, y + margin));
                if (!result.IsSuccess || !result.Text.Contains("鼠虎猴") || !result.Text.Contains("牛兔") ||
                    result.Text.Contains('房') || result.Words.Count != 6)
                    failures.Add($"scale={scale}, margin={margin}: {result.Text.ReplaceLineEndings(" / ")} ({result.Words.Count} words)");
            }
        }
        if (Environment.GetEnvironmentVariable("SNAPCUT_ARTISTIC_DISPLAY_SAMPLE") is { } displayPath && File.Exists(displayPath))
        {
            using var display = new Bitmap(displayPath);
            using var crop = new CapturedImage(display.Clone(new Rectangle(9, 19, 293, 401), System.Drawing.Imaging.PixelFormat.Format32bppArgb));
            var result = await HighQualityOcrService.RecognizeAsync(crop, manager);
            Console.WriteLine($"artistic actual display: {JsonSerializer.Serialize(result.Text)}");
            AssertMappedWords(result, crop);
            if (!result.Text.Contains("鼠虎猴") || !result.Text.Contains("牛兔") || result.Text.Contains('房') || result.Words.Count != 6)
                failures.Add($"actual display: {result.Text.ReplaceLineEndings(" / ")} ({result.Words.Count} words)");
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    private static void AssertMappedWords(OcrRecognitionResult result, CapturedImage image) =>
        Assert.All(result.Words, word =>
        {
            Assert.InRange(word.X, 0, image.Bitmap.Width);
            Assert.InRange(word.Y, 0, image.Bitmap.Height);
            Assert.InRange(word.X + word.Width, 1, image.Bitmap.Width);
            Assert.InRange(word.Y + word.Height, 1, image.Bitmap.Height);
            Assert.True(word.Width > 0 && word.Height > 0);
        });

    private static Process Worker() => (Process)typeof(OcrWorkerProcess)
        .GetField("_process", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

    private sealed class ArtisticSampleFactAttribute : FactAttribute
    {
        public ArtisticSampleFactAttribute()
        {
            if (!File.Exists(Environment.GetEnvironmentVariable("SNAPCUT_ARTISTIC_OCR_SAMPLE")) ||
                !Directory.Exists(Environment.GetEnvironmentVariable("SNAPCUT_OCR_MODEL_DIRECTORY")))
                Skip = "Provide local sample and installed models to run actual artistic OCR; no user image is bundled.";
        }
    }
}
