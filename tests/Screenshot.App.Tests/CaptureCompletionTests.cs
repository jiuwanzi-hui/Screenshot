using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using Screenshot.App.Capture;
using Screenshot.App.Core;
using Screenshot.App.Pin;

namespace Screenshot.App.Tests;

public sealed class CaptureCompletionTests
{
    private static CaptureOverlayOptions Options(string directory, PinnedImageManager pins, CaptureHistoryService history,
        bool save = false, bool keepHistory = true, PngSaveLocationMode mode = PngSaveLocationMode.DefaultDirectory) => new()
    {
        SaveDirectory = directory, KeepHistory = keepHistory, HistoryLimit = 5, HistoryService = history,
        PinnedImageManager = pins, StartOcrAsync = _ => Task.CompletedTask,
        ScreenshotScalePercent = 50, AutoSaveOnComplete = save, PngSaveLocationMode = mode,
    };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompletionCopiesAndReturnsTheSameScaledImageAndHonorsSaveAndHistory(bool save)
    {
        var directory = Directory.CreateTempSubdirectory("SnapCut-Completion-");
        using var pins = new PinnedImageManager();
        var history = new CaptureHistoryService(directory.FullName);
        using var source = new CapturedImage(new System.Drawing.Bitmap(160, 100));
        BitmapSource? copied = null;
        try
        {
            var result = await CaptureCompletionService.CompleteAsync(source, Options(directory.FullName, pins, history, save),
                () => throw new InvalidOperationException("Unexpected picker"),
                image => { copied = image; return Task.CompletedTask; });
            Assert.Same(copied, result);
            Assert.True(result!.IsFrozen);
            Assert.Equal(80, result.PixelWidth); Assert.Equal(50, result.PixelHeight);
            var item = Assert.Single(history.Items);
            Assert.Equal(80, item.PixelWidth); Assert.Equal(50, item.PixelHeight);
            if (save)
            {
                Assert.True(File.Exists(item.SavedPath));
                using var saved = new System.Drawing.Bitmap(item.SavedPath!);
                Assert.Equal(80, saved.Width); Assert.Equal(50, saved.Height);
            }
            else Assert.Null(item.SavedPath);
        }
        finally
        {
            history.Clear();
            // History offload owns its temporary cache; remove only our PNG saves.
            foreach (var file in directory.EnumerateFiles("Screenshot*.png")) file.Delete();
        }
    }

    [Fact]
    public async Task CancelledSavePickerClosesOverlayBeforePickerAndDoesNotCopyOrAddHistory()
    {
        using var pins = new PinnedImageManager();
        var history = new CaptureHistoryService();
        using var source = new CapturedImage(new System.Drawing.Bitmap(40, 40));
        var closed = false;
        var result = await CaptureCompletionService.CompleteAsync(source,
            Options(Path.GetTempPath(), pins, history, save: true, mode: PngSaveLocationMode.AskEveryTime),
            () => closed = true, _ => throw new InvalidOperationException("Should not copy"),
            (_, _) => { Assert.True(closed); return Task.FromResult<string?>(null); });
        Assert.Null(result); Assert.Empty(history.Items);
    }

    [Fact]
    public async Task FailedClipboardCannotCommitHistoryOrReturnAnAiAttachment()
    {
        using var pins = new PinnedImageManager();
        var history = new CaptureHistoryService();
        using var source = new CapturedImage(new System.Drawing.Bitmap(40, 40));
        await Assert.ThrowsAsync<IOException>(() => CaptureCompletionService.CompleteAsync(source,
            Options(Path.GetTempPath(), pins, history), () => { }, _ => throw new IOException("busy")));
        Assert.Empty(history.Items);
    }

    [Fact]
    public async Task DisabledSaveAndHistoryStillReturnIndependentAiImage()
    {
        using var pins = new PinnedImageManager();
        var history = new CaptureHistoryService();
        var source = new CapturedImage(new System.Drawing.Bitmap(40, 40));
        var result = await CaptureCompletionService.CompleteAsync(source,
            Options(Path.GetTempPath(), pins, history, keepHistory: false), () => { }, _ => Task.CompletedTask);
        source.Dispose();
        Assert.Equal(20, result!.PixelWidth); Assert.True(result.IsFrozen); Assert.Empty(history.Items);
    }

}
