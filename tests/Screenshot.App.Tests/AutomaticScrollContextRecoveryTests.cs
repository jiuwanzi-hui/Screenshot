using System.Drawing;
using System.Drawing.Imaging;
using System.Threading.Channels;
using Screenshot.App.Capture;

namespace Screenshot.App.Tests;

public sealed class AutomaticScrollContextRecoveryTests
{
    [Fact]
    public void MissingBodyAndContextEvidenceCannotInventMovementOrReplaceAnchor()
    {
        using var document = CreateDocument();
        using var blank = new Bitmap(document.Width, document.Height, PixelFormat.Format32bppPArgb);
        using var context = CreateContext(document.Width);
        using var initial = Capture(context, document, 0, 0);
        using var lost = Capture(context, blank, 0, 1);
        Assert.Null(ScrollCapturePosition.Read(lost)!.Rows);
        using var recovered = Capture(context, document, 56, 2);
        Assert.Equal(56, ScrollCapturePosition.Difference(initial, recovered));
    }

    [Fact]
    public void BlankSelectionAndUnrelatedPanelsRemainUnverified()
    {
        using var blank = new Bitmap(640, 1104, PixelFormat.Format32bppPArgb);
        using var context = CreateContext(blank.Width);
        using var initial = Capture(context, blank, 0, 0);
        using var next = Capture(context, blank, 28, 1);
        Assert.Null(ScrollCapturePosition.Read(next)!.Rows);
        using var composer = new ControlledScrollCaptureComposer();
        composer.Initialize(initial, ScrollCaptureOptions.Default);
        Assert.False(composer.TryAddDown(next, ScrollCaptureOptions.Default, 28));
        Assert.Equal(SelectionHeight, composer.OutputHeight);
        Assert.Equal("automatic-context-unverified", composer.LastRejectReason);
    }

    [Fact]
    public void ChangingOutsidePanelsCannotInvalidateSelectedContent()
    {
        using var document = CreateDocument();
        using var context = CreateContext(document.Width);
        using var composer = new ControlledScrollCaptureComposer();
        using var initial = Capture(context, document, 0, 0);
        composer.Initialize(initial, ScrollCaptureOptions.Default);
        for (var top = 28; top <= 280; top += 28)
        {
            using var frame = Capture(context, document, top, top / 28);
            Assert.Equal(top, ScrollCapturePosition.Difference(initial, frame));
            Assert.True(composer.TryAddDown(frame, ScrollCaptureOptions.Default, 10), composer.LastRejectReason);
            Assert.Equal(28, composer.LastFrameMovementRows);
        }
        using var output = composer.Compose();
        using var expected = document.Clone(new Rectangle(0, 0, document.Width, SelectionHeight + 280), PixelFormat.Format32bppPArgb);
        ScrollCaptureFixedRegionsTests.AssertPixelsEqual(expected, output);
    }

    [Fact]
    public async Task ServiceKeepsMovingAndReturnsWithChangingOutsidePanels()
    {
        using var document = CreateDocument();
        using var context = CreateContext(document.Width);
        var top = 252;
        var sample = 0;
        var gate = new object();
        Bitmap NextFrame()
        {
            lock (gate) return Capture(context, document, top, sample++);
        }
        var states = new List<ControlledScrollCaptureState>();
        var corrections = 0;
        var actions = Channel.CreateUnbounded<ScrollCapturePointerAction>();
        actions.Writer.TryWrite(ScrollCapturePointerAction.Click);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var result = await ScrollCaptureService.CaptureControlledCoreAsync(
            new(new(1), new(1), new(0, 0, document.Width, SelectionHeight), false), completion.Task, actions.Reader,
            initialFrame: NextFrame(),
            driverFactory: target => new ControlledScrollDriver(target, (_, delta) =>
            {
                lock (gate) top = Math.Clamp(top - delta / 2, 0, document.Height - SelectionHeight);
                return true;
            }, _ => NextFrame()),
            stateChanged: state =>
            {
                states.Add(state);
                if (state == ControlledScrollCaptureState.BottomReached)
                    actions.Writer.TryWrite(ScrollCapturePointerAction.DoubleClick);
                if (state is ControlledScrollCaptureState.TopReached or ControlledScrollCaptureState.FinalTopReached or
                    ControlledScrollCaptureState.PausedDown or ControlledScrollCaptureState.PausedUp)
                    completion.TrySetResult();
            },
            recoveryDirectionChanged: direction => { if (direction is not null) corrections++; },
            cancellationToken: timeout.Token);
        Assert.True(result.IsSuccess, $"{result.ErrorMessage}; top={top}; samples={sample}; corrections={corrections}; states={string.Join(',', states)}");
        using var image = Assert.IsType<CapturedImage>(result.Image);
        Assert.Contains(ControlledScrollCaptureState.BottomReached, states);
        Assert.DoesNotContain(ControlledScrollCaptureState.PausedDown, states);
        Assert.DoesNotContain(ControlledScrollCaptureState.PausedUp, states);
        Assert.Equal(0, corrections);
        ScrollCaptureFixedRegionsTests.AssertPixelsEqual(document, image.Bitmap);
    }

    [CodeEditorSampleTheory]
    [InlineData(5)]
    [InlineData(28)]
    public void RealCodePixelsSurviveUnrelatedSurroundingChanges(int step)
    {
        using var original = new Bitmap(Environment.GetEnvironmentVariable("SNAPCUT_CODE_SCROLL_SAMPLE")!);
        using var document = original.Clone(new Rectangle(260, 160, 1700, 1130), PixelFormat.Format32bppPArgb);
        using var context = CreateContext(document.Width);
        using var initial = Capture(context, document, 0, 0);
        for (var top = step; top < document.Height - SelectionHeight; top += step)
        {
            using var frame = Capture(context, document, top, top / step);
            Assert.Equal(top, ScrollCapturePosition.Difference(initial, frame));
        }
    }

    private const int SelectionHeight = 600;
    private const int OutsideHeight = 350;

    private static ScrollCaptureContext CreateContext(int width) =>
        new(new(0, 0, width, SelectionHeight + OutsideHeight * 2), new(0, OutsideHeight, width, SelectionHeight),
            preferSelectedContent: true);

    private static Bitmap Capture(ScrollCaptureContext context, Bitmap document, int top, int sample)
    {
        using var surrounding = new Bitmap(document.Width, SelectionHeight + OutsideHeight * 2, PixelFormat.Format32bppPArgb);
        using (var graphics = Graphics.FromImage(surrounding))
        {
            // The surrounding strip includes independent IDE panels/sticky
            // headings. These pixels are outside the user's selected result.
            using var font = new Font("Consolas", 12);
            graphics.Clear(Color.FromArgb(28, 29, 32));
            for (var y = 0; y < surrounding.Height; y += 22)
            for (var x = 0; x < surrounding.Width; x += 160)
                graphics.DrawString($"panel {sample * 173 + y}-{x}", font,
                    sample % 2 == 0 ? Brushes.LightGray : Brushes.SteelBlue, x, y);
            graphics.DrawImage(document, new Rectangle(0, OutsideHeight, document.Width, SelectionHeight),
                new Rectangle(0, top, document.Width, SelectionHeight), GraphicsUnit.Pixel);
        }
        return context.Extract(surrounding);
    }

    private static Bitmap CreateDocument()
    {
        var document = new Bitmap(640, 1104, PixelFormat.Format32bppPArgb);
        using var graphics = Graphics.FromImage(document);
        using var font = new Font("Consolas", 12);
        graphics.Clear(Color.FromArgb(30, 31, 34));
        for (var y = 0; y < document.Height; y += 28)
            graphics.DrawString($"{y:D4}  registerControlBlock(frame_{y}, mapping_{y * 7});", font,
                y % 56 == 0 ? Brushes.CadetBlue : Brushes.Tan, 6, y);
        return document;
    }
}
