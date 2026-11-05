using System.Drawing;
using System.Drawing.Imaging;
using System.Threading.Channels;
using Screenshot.App.Capture;

namespace Screenshot.App.Tests;

public sealed class AutomaticCodeEditorReplayTests
{
    [CodeEditorSampleTheory]
    [InlineData(120, 84)]
    [InlineData(40, 196)]
    public async Task BufferedLineScrollingKeepsAdvancingWithoutRecovery(int wheelUnitsPerStep, int rowStep)
    {
        using var original = new Bitmap(Environment.GetEnvironmentVariable("SNAPCUT_CODE_SCROLL_SAMPLE")!);
        using var document = original.Clone(new Rectangle(178, 133, 2000, 1200), PixelFormat.Format32bppPArgb);
        const int viewport = 640;
        var gate = new object();
        var top = 0;
        var pendingWheel = 0;
        var reverseInputs = 0;
        var paused = false;
        var corrections = 0;
        var actions = Channel.CreateUnbounded<ScrollCapturePointerAction>();
        actions.Writer.TryWrite(ScrollCapturePointerAction.Click);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result = await ScrollCaptureService.CaptureControlledCoreAsync(
            new(new(1), new(1), new(0, 0, document.Width, viewport), false), completion.Task, actions.Reader,
            stateChanged: state =>
            {
                if (state != ControlledScrollCaptureState.PausedDown) return;
                paused = true; completion.TrySetResult();
            },
            previewChanged: preview => { if (preview.PixelHeight == document.Height) completion.TrySetResult(); },
            initialFrame: document.Clone(new Rectangle(0, 0, document.Width, viewport), PixelFormat.Format32bppPArgb),
            driverFactory: target => new ControlledScrollDriver(target, (_, delta) =>
            {
                lock (gate)
                {
                    if (delta > 0) reverseInputs++;
                    pendingWheel -= delta;
                    while (Math.Abs(pendingWheel) >= wheelUnitsPerStep)
                    {
                        var sign = Math.Sign(pendingWheel);
                        pendingWheel -= sign * wheelUnitsPerStep;
                        top = Math.Clamp(top + sign * rowStep, 0, document.Height - viewport);
                    }
                }
                return true;
            }, _ => { lock (gate) return document.Clone(new Rectangle(0, top, document.Width, viewport), PixelFormat.Format32bppPArgb); }),
            recoveryDirectionChanged: direction => { if (direction is not null) corrections++; },
            cancellationToken: timeout.Token);
        Assert.True(result.IsSuccess, result.ErrorMessage);
        using var image = Assert.IsType<CapturedImage>(result.Image);
        Assert.False(paused);
        Assert.Equal(0, reverseInputs);
        Assert.Equal(0, corrections);
        ScrollCaptureFixedRegionsTests.AssertPixelsEqual(document, image.Bitmap);
    }

    [CodeEditorSampleTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void CodeEditorKeepsConnectingAcrossLineStepsAndCaretRepaint(bool repaint)
    {
        using var original = new Bitmap(Environment.GetEnvironmentVariable("SNAPCUT_CODE_SCROLL_SAMPLE")!);
        using var document = original.Clone(new Rectangle(178, 133, 2000, 1200), PixelFormat.Format32bppPArgb);
        const int height = 640;
        using var initial = document.Clone(new Rectangle(0, 0, document.Width, height), PixelFormat.Format32bppPArgb);
        var options = ScrollCaptureOptions.Default with { MinimumNewRows = 1 };
        using var composer = new ControlledScrollCaptureComposer();
        composer.Initialize(initial, options);
        var top = 0;
        var count = 0;
        int[] steps = [28, 56, 14, 67, 27];
        while (top < document.Height - height)
        {
            var step = Math.Min(steps[count++ % steps.Length], document.Height - height - top);
            top += step;
            using var frame = document.Clone(new Rectangle(0, top, document.Width, height), PixelFormat.Format32bppPArgb);
            if (repaint)
            {
                using var graphics = Graphics.FromImage(frame);
                // A code editor may repaint a blinking caret independently of
                // its scroll. It cannot invalidate the other complete lines.
                graphics.FillRectangle(count % 2 == 0 ? Brushes.LightGray : Brushes.Gray, 1000, 250, 2, 20);
            }
            Assert.True(composer.TryAddDown(frame, options, expectedRows: step, maximumAcceptedNewRows: 240),
                $"top={top}, step={step}, reason={composer.LastRejectReason}, movement={composer.LastFrameMovementRows}");
            Assert.Equal(step, composer.LastFrameMovementRows);
        }
        using var result = composer.Compose();
        if (!repaint) ScrollCaptureFixedRegionsTests.AssertPixelsEqual(document, result);
        Assert.Equal(document.Size, result.Size);
    }
}

internal sealed class CodeEditorSampleTheoryAttribute : TheoryAttribute
{
    public CodeEditorSampleTheoryAttribute()
    {
        if (!System.IO.File.Exists(Environment.GetEnvironmentVariable("SNAPCUT_CODE_SCROLL_SAMPLE")))
            Skip = "Requires the user's code-editor screenshot replay fixture.";
    }
}
