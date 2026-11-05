using System.Drawing;
using System.Drawing.Imaging;
using System.Threading.Channels;
using Screenshot.App.Capture;

namespace Screenshot.App.Tests;

public sealed class AutomaticScrollRepeatedRecoveryTests
{
    [Fact]
    public async Task ReconnectingToTheSameOldViewportCannotRestartRecoveryForever()
    {
        using var document = new Bitmap(180, 700, PixelFormat.Format32bppPArgb);
        var random = new Random(92103);
        for (var y = 0; y < document.Height; y++)
        for (var x = 0; x < document.Width; x++)
            document.SetPixel(x, y, Color.FromArgb(255, random.Next(256), random.Next(256), random.Next(256)));
        using var initial = document.Clone(new Rectangle(0, 0, 180, 180), PixelFormat.Format32bppPArgb);
        using var unrelated = document.Clone(new Rectangle(0, 400, 180, 180), PixelFormat.Format32bppPArgb);
        var gate = new object();
        var lost = false;
        var recoveries = 0;
        var paused = false;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var actions = Channel.CreateUnbounded<ScrollCapturePointerAction>();
        actions.Writer.TryWrite(ScrollCapturePointerAction.Click);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var target = new ScrollCaptureTarget(new(1), new(1), new(0, 0, 180, 180), false);
        var result = await ScrollCaptureService.CaptureControlledCoreAsync(target, completion.Task, actions.Reader,
            options: ScrollCaptureOptions.Default with { MinimumNewRows = 1 },
            stateChanged: state =>
            {
                if (state == ControlledScrollCaptureState.PausedDown)
                { paused = true; completion.TrySetResult(); }
            },
            initialFrame: new Bitmap(initial),
            driverFactory: captureTarget => new ControlledScrollDriver(captureTarget, (_, delta) =>
            {
                lock (gate)
                {
                    if (delta > 0) { recoveries++; lost = false; }
                    else lost = true;
                }
                return true;
            }, _ => { lock (gate) return new Bitmap(lost ? unrelated : initial); }),
            cancellationToken: timeout.Token);
        Assert.True(result.IsSuccess, result.ErrorMessage);
        using var image = Assert.IsType<CapturedImage>(result.Image);
        Assert.True(paused, "An unrecoverable page must stop automatic input while preserving the result.");
        Assert.InRange(recoveries, 1, 2);
        ScrollCaptureFixedRegionsTests.AssertPixelsEqual(initial, image.Bitmap);
    }
}
