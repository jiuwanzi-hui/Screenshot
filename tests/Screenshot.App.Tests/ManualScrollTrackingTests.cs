using System.Drawing;
using System.Drawing.Imaging;
using Screenshot.App.Capture;

namespace Screenshot.App.Tests;

public sealed class ManualScrollTrackingTests
{
    private static readonly int[] ReversingPositions = [900, 1100, 900, 750, 600, 800, 1000, 1200];

    [Fact]
    public void ReversingPastAMissedFrameRecoversInsideAlreadyCapturedContent()
    {
        using var document = CreateNarrowDocument(760, 3000);
        using var composer = ScrollCaptureComposer.CreateForManualCapture();
        int[] positions = [400, 600, 800, 1000, 1200, 1400, 1600, 1800, 2000,
            1000, 800, 600, 400, 200, 1600, 1800, 2000, 2200];
        foreach (var top in positions)
        {
            using var frame = document.Clone(new Rectangle(0, top, 760, 400), PixelFormat.Format32bppPArgb);
            composer.TryAddFrame(frame, ScrollCaptureDirection.Down, ScrollCaptureOptions.Default, out _);
            Assert.True(composer.LastFrameMovementRows is not null,
                $"Lost at {top}: {composer.LastRejectReason}");
            Assert.Equal(top - 400, composer.CurrentFrameTop);
            if (top == 2000)
            {
                var capturedHeight = composer.OutputHeight;
                using var unseen = document.Clone(new Rectangle(0, 2600, 760, 400), PixelFormat.Format32bppPArgb);
                Assert.False(composer.TryAddFrame(unseen, ScrollCaptureDirection.Down,
                    ScrollCaptureOptions.Default, expectedNewRows: 600, lockDirection: true, out _));
                Assert.Null(composer.LastFrameMovementRows);
                Assert.Equal(capturedHeight, composer.OutputHeight);
            }
        }
        Assert.Equal(2400, composer.OutputHeight);
        using var result = composer.Compose();
        // Recovery is navigation through old pixels, not a duplicate append or
        // a guessed jump that silently leaves a missing strip at either edge.
        for (var y = 0; y < result.Height; y++)
            for (var x = 0; x < 130; x++)
                Assert.Equal(document.GetPixel(x, y + 200), result.GetPixel(x, y));
        Assert.False(composer.CreateLivePreviewState(200, 800).IsTrackingLost);
    }

    [Fact]
    public async Task PreviewPublishesTheLastExpansionAfterAReversingBurstStops()
    {
        using var document = CreateNarrowDocument(760, 2400);
        using var composer = ScrollCaptureComposer.CreateForManualCapture();
        using (var initial = document.Clone(new Rectangle(0, 800, 760, 400), PixelFormat.Format32bppPArgb))
            composer.TryAddFrame(initial, ScrollCaptureOptions.Default, out _);
        var heights = new System.Collections.Concurrent.ConcurrentQueue<int>();
        await using var publisher = new ScrollCapturePreviewPublisher(() =>
        {
            using var preview = composer.ComposeLivePreview(760, 3000);
            heights.Enqueue(preview.Height);
            return Task.CompletedTask;
        }, minimumIntervalMilliseconds: 80, CancellationToken.None);
        await publisher.RefreshAsync();
        await Task.Run(() =>
        {
            foreach (var top in ReversingPositions)
            {
                using var frame = document.Clone(new Rectangle(0, top, 760, 400), PixelFormat.Format32bppPArgb);
                if (composer.TryAddFrame(frame, ScrollCaptureDirection.Up, ScrollCaptureOptions.Default, out _))
                    publisher.Request();
            }
        });
        await publisher.RefreshAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1000, heights.Last());
        Assert.Equal(composer.OutputHeight, heights.Last());
    }

    [Fact]
    public void SparseNewPixelsAreNotDiscardedBeforeMatching()
    {
        using var gate = new CapturedFrameGate();
        using var first = new Bitmap(1268, 616, PixelFormat.Format32bppPArgb);
        using (var graphics = Graphics.FromImage(first)) graphics.Clear(Color.White);
        gate.Accept(first);
        using var next = (Bitmap)first.Clone();
        // A one-pixel movement/change can disappear completely in a 96x64
        // signature, especially near a newly crossed capture boundary.
        next.SetPixel(17, 3, Color.Black);
        Assert.True(gate.HasChanged(next));
        Assert.False(gate.HasChanged(first));
        Assert.True(gate.HasChanged(next));
        gate.AcceptPending();
        Assert.False(gate.HasChanged(next));
        Assert.True(gate.HasChanged(first));
    }

    [Fact]
    public void NarrowTextColumnStillRegistersOnAWidePage()
    {
        using var document = CreateNarrowDocument(1600, 2400);
        using var composer = ScrollCaptureComposer.CreateForManualCapture();
        foreach (var top in ReversingPositions.Prepend(800))
        {
            using var frame = document.Clone(new Rectangle(0, top, 1600, 400), PixelFormat.Format32bppPArgb);
            composer.TryAddFrame(frame, ScrollCaptureDirection.Up, ScrollCaptureOptions.Default,
                expectedNewRows: 40, lockDirection: true, out _);
            Assert.NotNull(composer.LastFrameMovementRows);
            Assert.Equal(top - 800, composer.CurrentFrameTop);
        }
        Assert.Equal(1000, composer.OutputHeight);
    }

    [Fact]
    public void ADisconnectedViewportCannotCreateASeamFromWheelEstimates()
    {
        using var document = CreateNarrowDocument(760, 2400);
        using var composer = ScrollCaptureComposer.CreateForManualCapture();
        using var first = document.Clone(new Rectangle(0, 400, 760, 400), PixelFormat.Format32bppPArgb);
        using var disconnected = document.Clone(new Rectangle(0, 1600, 760, 400), PixelFormat.Format32bppPArgb);
        composer.TryAddFrame(first, ScrollCaptureOptions.Default, out _);
        Assert.False(composer.TryAddFrame(disconnected, ScrollCaptureDirection.Down,
            ScrollCaptureOptions.Default, expectedNewRows: 1200, lockDirection: true, out _));
        Assert.Null(composer.LastFrameMovementRows);
        Assert.Equal(0, composer.CurrentFrameTop);
        Assert.Equal(400, composer.OutputHeight);
        Assert.True(composer.CreateLivePreviewState(200, 800).IsTrackingLost);
        using var recovery = document.Clone(new Rectangle(0, 300, 760, 400), PixelFormat.Format32bppPArgb);
        Assert.True(composer.TryAddFrame(recovery, ScrollCaptureDirection.Up, ScrollCaptureOptions.Default, out _));
        Assert.Equal(-100, composer.CurrentFrameTop);
        Assert.Equal(500, composer.OutputHeight);
        Assert.False(composer.CreateLivePreviewState(200, 800).IsTrackingLost);
    }

    private static Bitmap CreateNarrowDocument(int width, int height)
    {
        var document = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
        using var graphics = Graphics.FromImage(document);
        graphics.Clear(Color.White);
        using var font = new Font("Consolas", 12, GraphicsUnit.Pixel);
        for (var line = 0; line * 20 < height; line++)
            graphics.DrawString($"{4800 + line}  item", font, Brushes.Black, 15, line * 20);
        return document;
    }
}
