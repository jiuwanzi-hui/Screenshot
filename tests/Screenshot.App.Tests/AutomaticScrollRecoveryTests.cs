using System.Drawing;
using System.Drawing.Imaging;
using System.Threading.Channels;
using Screenshot.App.Capture;

namespace Screenshot.App.Tests;

public sealed class AutomaticScrollRecoveryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReturnLegRecoversAnUnmatchedJumpAndContinuesWithoutMissingRows(bool fixedBars)
    {
        const int width = 180, viewport = 180, initialTop = 650;
        var topRows = fixedBars ? 60 : 0;
        var bottomRows = fixedBars ? 80 : 0;
        using var document = new Bitmap(width, 1600, PixelFormat.Format32bppPArgb);
        var random = new Random(71031);
        for (var y = 0; y < document.Height; y++)
        for (var x = 0; x < width; x++)
            document.SetPixel(x, y, x == 50
                ? Color.FromArgb(255, y / 256, y % 256, (y * 13) % 256)
                : Color.FromArgb(255, random.Next(256), random.Next(256), random.Next(256)));
        var gate = new object();
        var top = initialTop;
        var state = ControlledScrollCaptureState.WaitingToStart;
        var upFrames = 0;
        var states = new List<ControlledScrollCaptureState>();
        var lastPreview = string.Empty;
        var jumped = false;
        var recoverySteps = 0;
        var requestedReturn = false;
        var unexpectedlyPaused = false;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var actions = Channel.CreateUnbounded<ScrollCapturePointerAction>();
        actions.Writer.TryWrite(ScrollCapturePointerAction.Click);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(14));
        var target = new ScrollCaptureTarget(new IntPtr(1), new IntPtr(1), new(0, 0, width, viewport + topRows + bottomRows), false);
        var result = await ScrollCaptureService.CaptureControlledCoreAsync(target, completion.Task, actions.Reader,
            options: ScrollCaptureOptions.Default with { MaximumFrames = 160, MinimumNewRows = 1 },
            stateChanged: next =>
            {
                lock (gate) { state = next; states.Add(next); }
                if (next == ControlledScrollCaptureState.FinalTopReached) completion.TrySetResult();
                if (next == ControlledScrollCaptureState.PausedUp)
                {
                    unexpectedlyPaused = true;
                    completion.TrySetResult();
                }
            },
            previewChanged: preview =>
            {
                lastPreview = $"above={preview.AddedAboveFrameCount}, below={preview.AddedBelowFrameCount}, height={preview.PixelHeight}";
                if (!requestedReturn && preview.AddedBelowFrameCount >= 5)
                {
                    requestedReturn = true;
                    actions.Writer.TryWrite(ScrollCapturePointerAction.DoubleClick);
                }
                // Sampling cadence is adaptive: nine accepted frames do not
                // imply a fixed pixel distance. Drive past the requested range
                // before completing, then verify every output pixel below.
                if (Volatile.Read(ref recoverySteps) > 0 && preview.AddedAboveFrameCount >= 9 &&
                    Volatile.Read(ref top) < initialTop - viewport * 2)
                    completion.TrySetResult();
            },
            initialFrame: ScrollCaptureFixedRegionsTests.CreateFrame(document, initialTop, viewport, topRows, bottomRows),
            driverFactory: captureTarget => new ControlledScrollDriver(captureTarget, (_, delta) =>
            {
                lock (gate)
                {
                    if (state == ControlledScrollCaptureState.ScrollingUp && delta == -120) recoverySteps++;
                    top = Math.Clamp(top - delta / 2, 0, document.Height - viewport);
                }
                return true;
            }, _ =>
            {
                lock (gate)
                {
                    if (state == ControlledScrollCaptureState.ScrollingUp && ++upFrames == 3)
                    {
                        top -= viewport + 45; // Simulate delayed editor animation exceeding overlap.
                        jumped = true;
                    }
                    return ScrollCaptureFixedRegionsTests.CreateFrame(document, top, viewport, topRows, bottomRows);
                }
            }), cancellationToken: timeout.Token);
        Assert.True(result.IsSuccess, $"{result.ErrorMessage}; states={string.Join(",", states)}, top={top}, upFrames={upFrames}, jumped={jumped}, recovery={recoverySteps}, preview={lastPreview}");
        using var image = Assert.IsType<CapturedImage>(result.Image);
        Assert.True(jumped);
        Assert.True(recoverySteps > 0, "Fixture must exercise automatic backtracking.");
        Assert.False(unexpectedlyPaused, $"Capture entered PausedUp without user input; top={top}, recovery={recoverySteps}, {lastPreview}.");
        var first = image.Bitmap.GetPixel(50, topRows);
        var resultTop = first.R * 256 + first.G;
        Assert.True(resultTop < initialTop - 200, $"resultTop={resultTop}, top={top}, recovery={recoverySteps}, preview={lastPreview}");
        using var expected = ScrollCaptureFixedRegionsTests.CreateFrame(document, resultTop,
            image.Bitmap.Height - topRows - bottomRows, topRows, bottomRows);
        ScrollCaptureFixedRegionsTests.AssertPixelsEqual(expected, image.Bitmap);
    }
}
