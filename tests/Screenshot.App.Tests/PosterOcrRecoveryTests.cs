using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using RapidOcrNet;
using Screenshot.App.Capture;
using Screenshot.App.Text;
using SkiaSharp;
using Xunit.Abstractions;

namespace Screenshot.App.Tests;

public sealed class PosterOcrRecoveryTests(ITestOutputHelper output)
{
    [Fact]
    public void CompleteReadableColumnsSkipTileSearchButIncompleteOrUncertainOnesDoNot()
    {
        var bounds = new SKRectI(0, 0, 40, 240);
        Assert.True(PosterOcrRecovery.IsReadableColumn(Block("春夏秋冬山水", 0, 0, 40, 240, .85f), bounds));
        Assert.True(PosterOcrRecovery.IsReadableColumn(Block("春夏秋冬", 0, 0, 40, 240, .99f), bounds));
        Assert.False(PosterOcrRecovery.IsReadableColumn(Block("错", 0, 0, 40, 240, .99f), bounds));
        Assert.False(PosterOcrRecovery.IsReadableColumn(Block("春夏秋冬山水", 0, 0, 40, 240, .6f), bounds));
    }

    private static readonly string[] PosterPhrases = ["優雅", "時尚", "美尚", "專業美容指導", "品質生活指南", "全球時尚資訊",
        "盡在掌握之中", "提升個人", "塑造自信", "最新尚圖鑒", "2026", "style-guide-online.com"];
    [Fact]
    public void PartialCapturesCanTriggerRecoveryWithoutMultipleColumns()
    {
        var horizontal = Enumerable.Range(0, 20).Select(i => Block("正常文字", 5, i * 40, 300, 28, .99f)).ToArray();
        Assert.False(PosterOcrRecovery.NeedsRecovery(900, 1000, horizontal));
        var vertical = new[] { Block("错", 0, 0, 45, 420, .4f), Block("错", 60, 0, 45, 420, .5f) };
        Assert.True(PosterOcrRecovery.NeedsRecovery(727, 940, vertical));
        Assert.False(PosterOcrRecovery.NeedsRecovery(4000, 3000, vertical));
        Assert.True(PosterOcrRecovery.NeedsRecovery(700, 900, vertical[..1]));
        using var source = new SKBitmap(440, 630);
        using var canvas = new SKCanvas(source);
        canvas.Clear(SKColors.White);
        var caption = new[] { Block("短句", 5, 580, 420, 30, .99f) };
        Assert.False(PosterOcrRecovery.NeedsRecovery(source, caption));
        using var paint = new SKPaint { Color = SKColors.Red };
        canvas.DrawRect(50, 100, 100, 120, paint);
        Assert.True(PosterOcrRecovery.NeedsRecovery(source, caption));
        Assert.False(PosterOcrRecovery.NeedsRecovery(100, 40, []));
    }

    [Fact]
    public void LayoutRejectsMissingDuplicatedAndLowConfidenceGlyphs()
    {
        var words = new[] { Word("春", 0), Word("夏", 64), Word("，", 128) };
        Assert.True(PosterOcrRecovery.IsCompleteLayout(words, 3));
        Assert.False(PosterOcrRecovery.IsCompleteLayout(words[..2], 3));
        Assert.False(PosterOcrRecovery.IsCompleteLayout([words[0], words[0], words[2]], 3));
        Assert.False(PosterOcrRecovery.IsCompleteLayout([words[0], words[2]], 2));
        words[1] = new WordBox { Text = "夏", Score = .5f, BoxPoints = words[1].BoxPoints };
        Assert.False(PosterOcrRecovery.IsCompleteLayout(words, 3));
    }

    [Fact]
    public void MappedWordsUseAdjustedCutsAndStayInsideOriginalColumn()
    {
        var word = Word("夏", 64);
        PosterOcrRecovery.MapWord(word, new(20, 10, 60, 210), [10, 58, 130, 210]);
        Assert.All(word.BoxPoints, point => { Assert.InRange(point.X, 20, 60); Assert.InRange(point.Y, 58, 130); });
    }

    [Fact]
    public void ColumnFragmentsJoinWithoutCrossingAdjacentColumns()
    {
        OcrTextRegion[] regions = [new("甲乙", 10, 10, 30, 100) { RecoveredVerticalColumn = true },
            new("丙丁", 12, 106, 30, 100) { RecoveredVerticalColumn = true },
            new("另一列", 55, 8, 30, 200) { RecoveredVerticalColumn = true }];
        var result = PosterOcrRecovery.JoinColumns(new(true, "", null) { Regions = regions });
        Assert.Collection(result.Regions, region => Assert.Equal("甲乙丙丁", region.Text), region => Assert.Equal("另一列", region.Text));
    }

    [PosterSampleFact]
    public async Task SubmittedPosterPreservesPixelsTextCoordinatesAndWorkerLifetime()
    {
        using var manager = new HighQualityOcrModelManager(Environment.GetEnvironmentVariable("SNAPCUT_OCR_MODEL_DIRECTORY"));
        using var source = new CapturedImage(new Bitmap(Environment.GetEnvironmentVariable("SNAPCUT_POSTER_OCR_SAMPLE")!));
        var before = source.Bitmap.GetPixel(231, 432);
        var watch = Stopwatch.StartNew();
        var first = await HighQualityOcrService.RecognizeAsync(source, manager);
        output.WriteLine($"cold: {watch.ElapsedMilliseconds} ms\n{first.Text}");
        AssertPoster(first, source);
        Assert.Equal(before, source.Bitmap.GetPixel(231, 432));
        using var worker = Process.GetProcessById(Worker().Id);
        watch.Restart();
        var warm = await HighQualityOcrService.RecognizeAsync(source, manager);
        output.WriteLine($"warm: {watch.ElapsedMilliseconds} ms");
        Assert.Equal(first.Text, warm.Text);
        Assert.Equal(first.Words, warm.Words);
        Assert.Equal(worker.Id, Worker().Id);
        worker.Refresh();
        output.WriteLine($"worker total working set={worker.WorkingSet64 / 1048576d:F1} MiB; peak={worker.PeakWorkingSet64 / 1048576d:F1} MiB; private commit={worker.PrivateMemorySize64 / 1048576d:F1} MiB");
        using var idle = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await worker.WaitForExitAsync(idle.Token);
        watch.Restart();
        var again = await HighQualityOcrService.RecognizeAsync(source, manager);
        output.WriteLine($"reload: {watch.ElapsedMilliseconds} ms");
        Assert.Equal(first.Text, again.Text);
        Assert.NotEqual(worker.Id, Worker().Id);
        using var cancelledWorker = Process.GetProcessById(Worker().Id);
        using (var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(300)))
            Assert.False((await HighQualityOcrService.RecognizeAsync(source, manager, cancel.Token)).IsSuccess);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await cancelledWorker.WaitForExitAsync(deadline.Token);
        AssertPoster(await HighQualityOcrService.RecognizeAsync(source, manager), source);
        using var final = Process.GetProcessById(Worker().Id);
        using var finalDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await final.WaitForExitAsync(finalDeadline.Token);
    }

    [PosterSampleFact]
    public async Task SubmittedPosterSurvivesScaleAndActualDisplayCapture()
    {
        using var manager = new HighQualityOcrModelManager(Environment.GetEnvironmentVariable("SNAPCUT_OCR_MODEL_DIRECTORY"));
        using var source = new CapturedImage(new Bitmap(Environment.GetEnvironmentVariable("SNAPCUT_POSTER_OCR_SAMPLE")!));
        var failures = new List<string>();
        foreach (var scale in new[] { 80, 125 })
        {
            using var scaled = source.CreateScaled(scale);
            var watch = Stopwatch.StartNew();
            var result = await HighQualityOcrService.RecognizeAsync(scaled, manager);
            output.WriteLine($"scale={scale}: {watch.ElapsedMilliseconds} ms\n{result.Text}");
            if (!CheckDegradedSample(result, scaled)) failures.Add($"scale={scale} fell below the measured recovery baseline.");
        }
        if (File.Exists(Environment.GetEnvironmentVariable("SNAPCUT_POSTER_OCR_DISPLAY_SAMPLE")))
        {
            using var full = new Bitmap(Environment.GetEnvironmentVariable("SNAPCUT_POSTER_OCR_DISPLAY_SAMPLE")!);
            using var crop = new CapturedImage(full.Clone(new Rectangle(487, 0, 754, 929), System.Drawing.Imaging.PixelFormat.Format32bppArgb));
            var result = await HighQualityOcrService.RecognizeAsync(crop, manager);
            output.WriteLine($"actual display:\n{result.Text}");
            if (!CheckDegradedSample(result, crop)) failures.Add("Display capture fell below the measured recovery baseline.");
        }
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    private bool CheckDegradedSample(OcrRecognitionResult result, CapturedImage image)
    {
        Assert.True(result.IsSuccess, result.ErrorMessage);
        var missing = PosterPhrases.Where(phrase => !result.Text.Contains(phrase, StringComparison.Ordinal)).ToArray();
        output.WriteLine($"Exact phrase coverage: {PosterPhrases.Length - missing.Length}/{PosterPhrases.Length}; still missing: {string.Join(", ", missing)}");
        AssertCoordinates(result, image);
        // This is a measured quality floor, not a claim of lossless OCR after
        // resampling. The original image has the stricter all-phrases assertion.
        return missing.Length <= 3 && result.Text.Contains("優雅", StringComparison.Ordinal) &&
            result.Text.Contains("美尚", StringComparison.Ordinal) && result.Text.Contains("最新尚圖鑒", StringComparison.Ordinal);
    }

    private static void AssertPoster(OcrRecognitionResult result, CapturedImage image)
    {
        Assert.True(result.IsSuccess, result.ErrorMessage);
        foreach (var expected in PosterPhrases)
            Assert.Contains(expected, result.Text);
        Assert.Contains(result.Regions, region => region.Text == "時尚");
        Assert.DoesNotContain("最盈尚", result.Text);
        AssertCoordinates(result, image);
    }

    private static void AssertCoordinates(OcrRecognitionResult result, CapturedImage image)
    {
        Assert.All(result.Words, word =>
        {
            Assert.InRange(word.X, 0, image.PixelWidth - 1); Assert.InRange(word.Y, 0, image.PixelHeight - 1);
            Assert.InRange(word.X + word.Width, 1, image.PixelWidth); Assert.InRange(word.Y + word.Height, 1, image.PixelHeight);
        });
    }

    private static Process Worker() => (Process)typeof(OcrWorkerProcess).GetField("_process", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
    private static TextBlock Block(string text, int x, int y, int width, int height, float score) =>
        new() { Text = text, BoxPoints = [new(x, y), new(x + width, y), new(x + width, y + height), new(x, y + height)], Chars = [text], CharScores = [score] };
    private static WordBox Word(string text, int x) => new() { Text = text, Score = .99f, BoxPoints = [new(x + 2, 2), new(x + 60, 2), new(x + 60, 60), new(x + 2, 60)] };
    private sealed class PosterSampleFactAttribute : FactAttribute
    {
        public PosterSampleFactAttribute()
        {
            if (!File.Exists(Environment.GetEnvironmentVariable("SNAPCUT_POSTER_OCR_SAMPLE")) ||
                !Directory.Exists(Environment.GetEnvironmentVariable("SNAPCUT_OCR_MODEL_DIRECTORY")))
                Skip = "Supply the local poster and installed models; no user image is bundled.";
        }
    }
}
