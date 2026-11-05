using System.Drawing;
using Screenshot.App.Capture;

namespace Screenshot.App.Tests;

public sealed class ControlledScrollDriverTests
{
    private static ScrollCaptureTarget Target => new(new IntPtr(1), new IntPtr(1), new(0, 0, 400, 300), false);

    [Fact]
    public async Task ASlowPresentedFrameDoesNotHoldUpWheelInput()
    {
        using var captureStarted = new ManualResetEventSlim();
        using var releaseCapture = new ManualResetEventSlim();
        await using var driver = new ControlledScrollDriver(Target, (_, _) => true, _ =>
        {
            captureStarted.Set();
            if (!releaseCapture.Wait(TimeSpan.FromSeconds(3))) throw new TimeoutException();
            return new Bitmap(2, 2);
        });
        driver.SetDirection(ScrollCaptureDirection.Down);
        var capture = Task.Run(driver.CaptureFrame);
        Assert.True(captureStarted.Wait(TimeSpan.FromSeconds(2)));
        try
        {
            var initialSteps = driver.InputStepCount;
            var deadline = Environment.TickCount64 + 1500;
            while (driver.InputStepCount - initialSteps < 6 && Environment.TickCount64 < deadline)
                await Task.Delay(20);
            Assert.True(driver.InputStepCount - initialSteps >= 6, "Capture blocked the wheel scheduler.");
        }
        finally { releaseCapture.Set(); }
        using var bitmap = await capture;
        driver.SetDirection(null);
        var stopped = driver.InputStepCount;
        await Task.Delay(100);
        Assert.Equal(stopped, driver.InputStepCount);
    }

    [Fact]
    public async Task AnExplicitRetryClearsATransientInputFailure()
    {
        var canSend = false;
        await using var driver = new ControlledScrollDriver(Target, (_, _) => Volatile.Read(ref canSend), _ => new Bitmap(2, 2));
        driver.SetDirection(ScrollCaptureDirection.Up);
        var deadline = Environment.TickCount64 + 1500;
        while (!driver.HasInputFailure && Environment.TickCount64 < deadline) await Task.Delay(20);
        Assert.True(driver.HasInputFailure);
        Volatile.Write(ref canSend, true);
        driver.RetryInput();
        driver.SetDirection(ScrollCaptureDirection.Up);
        deadline = Environment.TickCount64 + 1500;
        while (driver.InputStepCount == 0 && Environment.TickCount64 < deadline) await Task.Delay(20);
        Assert.False(driver.HasInputFailure);
        Assert.True(driver.InputStepCount > 0);
    }

    [Fact]
    public async Task BoundaryRecoverySendsARealDetent()
    {
        var probe = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var driver = new ControlledScrollDriver(Target, (_, delta) =>
        {
            if (Math.Abs(delta) >= 120) probe.TrySetResult(delta);
            return true;
        }, _ => new Bitmap(2, 2));
        driver.RequestBoundaryProbe();
        driver.SetDirection(ScrollCaptureDirection.Down);
        Assert.Equal(-120, await probe.Task.WaitAsync(TimeSpan.FromSeconds(2)));
    }
}
