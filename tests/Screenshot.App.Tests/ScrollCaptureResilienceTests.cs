using System.Drawing;
using System.Reflection;
using System.Windows;
using Screenshot.App.Capture;
using Screenshot.App.Infrastructure;

namespace Screenshot.App.Tests;

public sealed class ScrollCaptureResilienceTests
{
    [Fact]
    public async Task OverdueSamplesDoNotStarveWheelReversalsOrFinish()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var motionProbe = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var wheel = Task.FromResult(-120);
        var sample = Task.FromResult(1);
        for (var i = 0; i < 30; i++)
        {
            // A slow computer returns with the timer already overdue every
            // time, while packets (including reversed direction) are queued.
            Assert.Same(wheel, await ScrollCaptureService.WaitForWheelCaptureEventAsync(
                completion.Task, wheel, motionProbe.Task, sample, preferSample: false));
            Assert.Same(sample, await ScrollCaptureService.WaitForWheelCaptureEventAsync(
                completion.Task, wheel, motionProbe.Task, sample, preferSample: true));
            wheel = Task.FromResult(i % 2 == 0 ? 120 : -120);
        }
        completion.SetResult();
        Assert.Same(completion.Task, await ScrollCaptureService.WaitForWheelCaptureEventAsync(
            completion.Task, wheel, motionProbe.Task, sample, preferSample: true));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(400)]
    public async Task SlowFramesAndFailedPreviewsStillCompleteWithEveryCapturedRow(int frameDelay)
    {
        const int width = 180, viewport = 180, initialTop = 80, topBar = 24, bottomBar = 32;
        using var document = new Bitmap(width, 1600, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        var random = new Random(1387);
        for (var y = 0; y < document.Height; y++)
        for (var x = 0; x < width; x++)
            document.SetPixel(x, y, Color.FromArgb(255, random.Next(256), random.Next(256), random.Next(256)));
        var gate = new object();
        var top = initialTop;
        var previewCalls = 0;
        var observedFailure = false;
        var observedRecovery = false;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var actions = System.Threading.Channels.Channel.CreateUnbounded<ScrollCapturePointerAction>();
        actions.Writer.TryWrite(ScrollCapturePointerAction.Click);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(18));
        var target = new ScrollCaptureTarget(new IntPtr(1), new IntPtr(1), new(0, 0, width, viewport + topBar + bottomBar), false);
        var result = await ScrollCaptureService.CaptureControlledCoreAsync(target, completion.Task, actions.Reader,
            initialFrame: ScrollCaptureFixedRegionsTests.CreateFrame(document, initialTop, viewport, topBar, bottomBar),
            previewChanged: preview =>
            {
                if (++previewCalls <= 4) throw new InvalidOperationException("injected temporary preview failure");
                if (preview.PixelHeight >= viewport + topBar + bottomBar + 180) completion.TrySetResult();
            }, previewStatusChanged: status =>
            {
                if (status is not null) observedFailure = true;
                else if (observedFailure) observedRecovery = true;
            }, driverFactory: captureTarget => new ControlledScrollDriver(captureTarget, (_, delta) =>
            {
                lock (gate) top = Math.Clamp(top - delta / 2, 0, document.Height - viewport);
                return true;
            }, _ =>
            {
                if (frameDelay > 0) Thread.Sleep(frameDelay);
                lock (gate) return ScrollCaptureFixedRegionsTests.CreateFrame(document, top, viewport, topBar, bottomBar);
            }), cancellationToken: timeout.Token);
        Assert.True(result.IsSuccess, result.ErrorMessage);
        using var image = Assert.IsType<CapturedImage>(result.Image);
        Assert.True(observedFailure && observedRecovery);
        Assert.True(image.Bitmap.Height >= viewport + topBar + bottomBar + 180);
        using var expected = ScrollCaptureFixedRegionsTests.CreateFrame(document, initialTop,
            image.Bitmap.Height - topBar - bottomBar, topBar, bottomBar);
        ScrollCaptureFixedRegionsTests.AssertPixelsEqual(expected, image.Bitmap);
    }

    [Fact]
    public async Task InputSessionRemainsResponsiveWhileUiIsBusyAndStopsAfterDisposal()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var released = 0;
        var input = new SessionInputThread(() => { }, () => Interlocked.Increment(ref released));
        var ui = Task.Run(() => WpfTestHost.Invoke(() =>
        {
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
        }));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(3)));
            var processed = 0;
            await input.Dispatcher.InvokeAsync(() => processed++).Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(1, processed);
            Assert.False(ui.IsCompleted);
            input.Dispose();
            await input.Stopped.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(1, released);
        }
        finally { input.Dispose(); release.Set(); await ui; }
    }

    [Fact]
    public void InputSessionReleasesItsResourcesIfInitializationFails()
    {
        using var released = new ManualResetEventSlim();
        Assert.Throws<InvalidOperationException>(() => new SessionInputThread(
            () => throw new InvalidOperationException("test init failure"), released.Set));
        Assert.True(released.Wait(TimeSpan.FromSeconds(2)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SlowCaptureBoundsUnseenTravelAndAutomaticallyResumesOnNextFrame(bool returning)
    {
        var target = new ScrollCaptureTarget(new IntPtr(1), new IntPtr(1), new(0, 0, 400, 180), false);
        await using var driver = new ControlledScrollDriver(target, (_, _) => true, _ => new Bitmap(400, 180));
        var limit = ControlledScrollDriver.GetCaptureStepLimit(180, returning);
        driver.SetDirection(ScrollCaptureDirection.Down, fastReturn: returning);
        var deadline = Environment.TickCount64 + 2000;
        while (driver.InputStepCount < limit && Environment.TickCount64 < deadline) await Task.Delay(10);
        Assert.Equal(limit, driver.InputStepCount);
        // Simulate slow image processing without advancing the capture handshake.
        await Task.Delay(350);
        Assert.Equal(limit, driver.InputStepCount);
        Assert.False(driver.HasInputFailure);
        using var captured = driver.CaptureFrame();
        Assert.True(driver.LastCapturedBackpressureWaitTicks > 0);
        deadline = Environment.TickCount64 + 1500;
        while (driver.InputStepCount <= limit && Environment.TickCount64 < deadline) await Task.Delay(10);
        Assert.True(driver.InputStepCount > limit);
        driver.SetDirection(null);
    }

    [Fact]
    public void PreviewAtRoundedWorkAreaLimitStillAcceptsLaterFrames()
    {
        WpfTestHost.Invoke(() =>
        {
            var window = new ScrollCaptureProgressWindow();
            using var image = new CapturedImage(new Bitmap(240, 600));
            try
            {
                typeof(ScrollCaptureProgressWindow).GetField("_maximumHeightDip", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .SetValue(window, 479.5); // Window.Height is 480: typical native rounding / reduced work area.
                var first = new ScrollCapturePreviewState(image.Preview, 2, 0, 1, 240, 600);
                window.UpdatePreview(first);
                window.UpdatePreview(first with { FrameCount = 3, AddedBelowFrameCount = 2, PixelHeight = 900 });
                var label = (System.Windows.Controls.TextBlock)window.FindName("FrameCountText");
                Assert.Contains("3 帧", label.Text);
                Assert.Contains("900", label.Text);
                Assert.Equal(480, window.Height);
            }
            finally { window.CloseFromCoordinator(); }
        });
    }
}
