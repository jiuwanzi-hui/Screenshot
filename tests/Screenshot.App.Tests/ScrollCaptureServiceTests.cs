using System.Drawing;
using System.Drawing.Imaging;
using Screenshot.App.Capture;

namespace Screenshot.App.Tests;

public sealed class ScrollCaptureServiceTests
{
    [Fact]
    public async Task ManualCaptureReturnsCanceledResult()
    {
        var completionSource = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();
        var target = new ScrollCaptureTarget(
            new IntPtr(1),
            new IntPtr(1),
            new ScreenRegion(0, 0, 320, 240),
            SupportsVerticalScroll: false);

        var result = await ScrollCaptureService.CaptureManualAsync(
            target,
            completionSource.Task,
            cancellationToken: cancellationSource.Token);

        Assert.False(result.IsSuccess);
        Assert.Null(result.Image);
        Assert.Equal("长截图已取消。", result.ErrorMessage);
    }

    [Fact]
    public void CreateInitialFrameCropsSelectionSnapshotToCaptureRegion()
    {
        using var selection = new Bitmap(200, 160, PixelFormat.Format32bppPArgb);
        using var graphics = Graphics.FromImage(selection);
        graphics.Clear(Color.White);
        using var brush = new SolidBrush(Color.FromArgb(10, 20, 30));
        graphics.FillRectangle(brush, new Rectangle(40, 20, 80, 100));

        using var frame = ScrollCaptureService.CreateInitialFrame(
            selection,
            selectionRegion: new ScreenRegion(100, 200, 200, 160),
            captureRegion: new ScreenRegion(140, 220, 80, 100));

        Assert.Equal(80, frame.Width);
        Assert.Equal(100, frame.Height);
        Assert.Equal(Color.FromArgb(10, 20, 30).ToArgb(), frame.GetPixel(0, 0).ToArgb());
        Assert.Equal(Color.FromArgb(10, 20, 30).ToArgb(), frame.GetPixel(79, 99).ToArgb());
    }

    [Fact]
    public void CreateInitialFrameClonesWhenRegionsMatch()
    {
        using var selection = new Bitmap(64, 96, PixelFormat.Format32bppPArgb);
        using var graphics = Graphics.FromImage(selection);
        graphics.Clear(Color.FromArgb(1, 2, 3));

        var region = new ScreenRegion(10, 20, 64, 96);
        using var frame = ScrollCaptureService.CreateInitialFrame(
            selection,
            selectionRegion: region,
            captureRegion: region);

        Assert.Equal(64, frame.Width);
        Assert.Equal(96, frame.Height);
        Assert.Equal(Color.FromArgb(1, 2, 3).ToArgb(), frame.GetPixel(0, 0).ToArgb());
        Assert.False(ReferenceEquals(selection, frame));
    }
    [Fact]
    public void CreateScrollCaptureTargetFromSelectionUsesExactSelectionRegion()
    {
        var selection = new ScreenRegion(80, 60, 240, 180);

        Assert.True(
            ForegroundWindowCaptureService.TryCreateScrollCaptureTargetFromSelection(
                selection,
                out var target));
        Assert.NotNull(target);
        Assert.Equal(selection, target!.CaptureRegion);
        Assert.NotEqual(IntPtr.Zero, target.WindowHandle);
        Assert.NotEqual(IntPtr.Zero, target.ScrollTargetHandle);
    }

    [Fact]
    public void CreateScrollCaptureTargetKeepsSelectionEvenWhenWindowHandleProvided()
    {
        var selection = new ScreenRegion(120, 90, 200, 160);
        var preferred = ForegroundWindowCaptureService.GetForegroundWindowHandle();

        Assert.True(
            ForegroundWindowCaptureService.TryCreateScrollCaptureTarget(
                preferred,
                selection,
                out var target));
        Assert.NotNull(target);
        Assert.Equal(selection, target!.CaptureRegion);
    }

    [Fact]
    public void CreateScrollCaptureTargetRejectsTinySelection()
    {
        Assert.False(
            ForegroundWindowCaptureService.TryCreateScrollCaptureTargetFromSelection(
                new ScreenRegion(10, 10, 20, 40),
                out var target));
        Assert.Null(target);
    }

    [Fact]
    public void DefaultScrollCaptureOptionsBalanceInteractiveUseAndRepeatedContent()
    {
        var options = ScrollCaptureOptions.Default;

        Assert.InRange(options.MinimumOverlapConfidence, 0.94, 0.96);
        Assert.True(options.MinimumNewRows <= 4);
        Assert.Equal(1, options.FrameDelayMilliseconds);
    }

    [Fact]
    public void WheelMotionTrackerAccumulatesDistanceAndResetsOnReversal()
    {
        var tracker = new ScrollWheelMotionTracker();

        tracker.AddDelta(-120);
        tracker.AddDelta(-240);
        Assert.Equal(-360, tracker.PendingDelta);
        Assert.Equal(ScrollCaptureDirection.Down, tracker.Direction);

        tracker.AddDelta(120);
        Assert.Equal(120, tracker.PendingDelta);
        Assert.Equal(ScrollCaptureDirection.Up, tracker.Direction);
    }

    [Fact]
    public void WheelMotionTrackerLearnsPixelDistanceFromAcceptedFrames()
    {
        var tracker = new ScrollWheelMotionTracker();
        var options = ScrollCaptureOptions.Default;

        tracker.AddDelta(-240);
        tracker.ObserveMovement(180);
        tracker.AddDelta(-480);

        Assert.Equal(360, tracker.GetExpectedRows(900, options));
    }

    [Fact]
    public void WheelMotionTrackerTransfersEachWheelRunToOneCapturedFrame()
    {
        var tracker = new ScrollWheelMotionTracker();
        var options = ScrollCaptureOptions.Default;

        tracker.AddDelta(-240);
        var first = tracker.TakePendingMotion(
            frameHeight: 720,
            options,
            ScrollCaptureDirection.Up);
        var second = tracker.TakePendingMotion(
            frameHeight: 720,
            options,
            ScrollCaptureDirection.Down);

        Assert.True(first.HasFreshInput);
        Assert.Equal(ScrollCaptureDirection.Down, first.Direction);
        Assert.Equal(-240, first.Delta);
        Assert.NotNull(first.ExpectedRows);
        Assert.False(second.HasFreshInput);
        Assert.Equal(ScrollCaptureDirection.Down, second.Direction);
        Assert.Null(second.ExpectedRows);

        tracker.ObserveMovement(180, first.Delta);
        tracker.AddDelta(-480);
        Assert.Equal(360, tracker.GetExpectedRows(900, options));
    }
}
