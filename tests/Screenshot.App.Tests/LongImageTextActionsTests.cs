using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Screenshot.App.Capture;
using Screenshot.App.Core;
using Screenshot.App.Editor;
using Screenshot.App.Text;

namespace Screenshot.App.Tests;

public sealed class LongImageTextActionsTests
{
    [Fact]
    public async Task OverlappingTilesKeepBoundaryLinesExactlyOnceAndReleaseEachTile()
    {
        using var image = new CapturedImage(new System.Drawing.Bitmap(300, 5100));
        for (var y = 0; y < image.Bitmap.Height; y++) image.Bitmap.SetPixel(0, y, System.Drawing.Color.FromArgb(y / 256, y % 256, 0));
        int[] rows = [50, 1200, 1220, 2410, 2440, 3630, 3670, 4870, 5060];
        var calls = 0;
        System.Drawing.Bitmap? previous = null;
        var result = await TallImageOcrService.RecognizeAsync(image, (tile, _) =>
        {
            if (previous is not null) Assert.ThrowsAny<Exception>(() => previous.GetPixel(0, 0));
            previous = tile.Bitmap;
            calls++;
            Assert.InRange(tile.Bitmap.Height, 1, 1600);
            var pixel = tile.Bitmap.GetPixel(0, 0);
            var top = pixel.R * 256 + pixel.G;
            var regions = rows.Where(row => row >= top && row + 20 < top + tile.Bitmap.Height)
                .Select(row => new OcrTextRegion($"row {row}", 12, row - top, 200, 20)).ToArray();
            return Task.FromResult(new OcrRecognitionResult(true, string.Join("\n", regions.Select(region => region.Text)), null)
                { Regions = regions, Words = regions.Select(region => new OcrWordRegion(region.Text, region.X, region.Y, region.Width, region.Height)).ToArray() });
        }, CancellationToken.None);
        Assert.True(result.IsSuccess);
        Assert.Equal(5, calls);
        Assert.Equal(rows.Select(row => (double)row), result.Regions.Select(region => region.Y));
        Assert.Equal(rows.Length, result.Words.Count);
        Assert.ThrowsAny<Exception>(() => previous!.GetPixel(0, 0));
    }

    [Fact]
    public async Task FailedTileIsNotReportedAsCompleteAndCancellationStopsFurtherTiles()
    {
        using var image = new CapturedImage(new System.Drawing.Bitmap(250, 4800));
        var calls = 0;
        var failed = await TallImageOcrService.RecognizeAsync(image, (_, _) => Task.FromResult(++calls == 2
            ? OcrRecognitionResult.Failure("engine failed") : new OcrRecognitionResult(true, "", null)), CancellationToken.None);
        Assert.False(failed.IsSuccess);
        Assert.Contains("第 2 部分", failed.ErrorMessage);
        Assert.Equal(2, calls);
        using var cancellation = new CancellationTokenSource();
        calls = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => TallImageOcrService.RecognizeAsync(image, (_, _) =>
        {
            calls++; cancellation.Cancel();
            return Task.FromResult(new OcrRecognitionResult(true, "", null));
        }, cancellation.Token));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task EditorReusesOcrAndRetriesOnlyFailedTranslationThenInvalidatesTarget()
    {
        ImageEditorWindow? window = null;
        Task operation = Task.CompletedTask;
        var calls = new List<int>();
        var ocrCalls = 0;
        WpfTestHost.Invoke(() =>
        {
            window = new ImageEditorWindow(new CapturedImage(new System.Drawing.Bitmap(400, 300)), System.IO.Path.GetTempPath(),
                recognizeTextWithCancellationAsync: (_, _) =>
                {
                    ocrCalls++;
                    return Task.FromResult(new OcrRecognitionResult(true, "First sentence\nSecond sentence", null)
                    { Regions = [new("First sentence", 10, 10, 200, 20), new("Second sentence", 10, 200, 200, 20)] });
                }, translateTextAsync: (input, _) =>
                {
                    calls.Add(input.Regions.Count);
                    return Task.FromResult(calls.Count == 1
                        ? new TranslationSegmentsResult(true, ["第一句", "Second sentence"], "partial") { FailedSegmentIndices = [1], Notice = "test model" }
                        : new TranslationSegmentsResult(true, ["第二句"], null) { FailedSegmentIndices = [], Notice = "test model" });
                });
            typeof(ImageEditorWindow).GetMethod("InitializeEditorCanvas", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
            operation = window.RunTextActionAsync(ImageTextAction.Translate);
        });
        try
        {
            await operation;
            WpfTestHost.Invoke(() => operation = window!.RunTextActionAsync(ImageTextAction.Translate));
            await operation;
            WpfTestHost.Invoke(() =>
            {
                Assert.Equal([2, 1], calls);
                Assert.Equal(1, ocrCalls);
                var canvas = (ImageEditorCanvas)window!.FindName("EditorCanvas");
                Assert.True(canvas.HasTranslationOverlay);
                typeof(ImageEditorWindow).GetMethod("OnTextTargetChanged", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [null, EventArgs.Empty]);
                Assert.False(canvas.HasTranslationOverlay);
                Assert.NotNull(((Button)window.FindName("TranslateTextButton")).ContextMenu);
            });
        }
        finally { WpfTestHost.Invoke(() => window?.Close()); }
    }

    [Fact]
    public async Task ClosingEditorCancelsRecognitionAndDiscardsLateResults()
    {
        ImageEditorWindow? window = null;
        Task operation = Task.CompletedTask;
        var started = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var result = new TaskCompletionSource<OcrRecognitionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        System.Drawing.Bitmap? snapshot = null;
        var translateCalls = 0;
        WpfTestHost.Invoke(() =>
        {
            window = new ImageEditorWindow(new CapturedImage(new System.Drawing.Bitmap(400, 300)), System.IO.Path.GetTempPath(),
                recognizeTextWithCancellationAsync: (image, token) =>
                { snapshot = image.Bitmap; started.SetResult(token); return result.Task; },
                translateTextAsync: (_, _) =>
                { translateCalls++; return Task.FromResult(new TranslationSegmentsResult(true, ["译文"], null)); });
            typeof(ImageEditorWindow).GetMethod("InitializeEditorCanvas", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
            operation = window.RunTextActionAsync(ImageTextAction.Translate);
        });
        var token = await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        WpfTestHost.Invoke(() => window!.Close());
        Assert.True(token.IsCancellationRequested);
        result.SetResult(new OcrRecognitionResult(true, "Source text", null)
            { Regions = [new("Source text", 10, 10, 100, 20)] });
        await operation.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, translateCalls);
        Assert.ThrowsAny<Exception>(() => snapshot!.GetPixel(0, 0));
    }

    [OcrPerformanceFact]
    public async Task RealOcrPreservesTextAcrossTallImageTileBoundaries()
    {
        using var manager = new HighQualityOcrModelManager(Environment.GetEnvironmentVariable("SNAPCUT_OCR_MODEL_DIRECTORY"));
        using var image = new CapturedImage(new System.Drawing.Bitmap(620, 5100));
        int[] rows = [40, 1180, 1225, 2395, 2442, 3615, 3665, 4835, 4885, 5040];
        using (var graphics = System.Drawing.Graphics.FromImage(image.Bitmap))
        using (var font = new System.Drawing.Font("Segoe UI", 18))
        {
            graphics.Clear(System.Drawing.Color.White);
            for (var i = 0; i < rows.Length; i++)
                graphics.DrawString($"Boundary {i + 101} complete text", font, System.Drawing.Brushes.Black, 12, rows[i]);
        }
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var result = await TallImageOcrService.RecognizeAsync(image,
            (tile, token) => HighQualityOcrService.RecognizeAsync(tile, manager, cancellationToken: token), CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorMessage);
        for (var i = 0; i < rows.Length; i++)
        {
            var matches = result.Regions.Where(region => region.Text.Contains((i + 101).ToString(System.Globalization.CultureInfo.InvariantCulture))).ToArray();
            Assert.Single(matches);
            Assert.InRange(matches[0].Y, rows[i] - 10, rows[i] + 30);
        }
        if (Environment.GetEnvironmentVariable("SNAPCUT_OCR_REPORT") is { Length: > 0 } path)
            await System.IO.File.WriteAllTextAsync(path, System.Text.Json.JsonSerializer.Serialize(new
                { dimensions = "620x5100", seconds = watch.Elapsed.TotalSeconds, lines = result.Regions.Count,
                  threads = HeavyWorkloadBudget.OcrThreadCount, result.Text }));
    }

    [Fact]
    public void PreviewEditFinishesCaptureAndOnlyRequestsTheEditor()
    {
        WpfTestHost.Invoke(() =>
        {
            var window = new ScrollCaptureProgressWindow();
            try
            {
                var count = 0;
                window.EditRequested += (_, _) => count++;
                typeof(ScrollCaptureProgressWindow).GetMethod("OnEditClick", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(window, [window, new RoutedEventArgs()]);
                Assert.Equal(1, count);
                Assert.False(((StackPanel)window.FindName("ActionPanel")).IsEnabled);
                Assert.False(window.IsVisible);
            }
            finally { window.CloseFromCoordinator(); }
        });
    }
}
