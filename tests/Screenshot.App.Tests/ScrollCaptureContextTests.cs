using System.Drawing;
using System.Drawing.Imaging;
using System.Threading.Channels;
using Screenshot.App.Capture;

namespace Screenshot.App.Tests;

public sealed class ScrollCaptureContextTests
{
    [Fact]
    public async Task AutomaticSmallSelectionCrossesBlankThenReturnsWithoutPrematurePause()
    {
        using var document = CreateDocument();
        const int viewport = 900, selectionTop = 500, selectionHeight = 234;
        var top = (document.Height - viewport) / 2;
        var gate = new object();
        using var context = new ScrollCaptureContext(new(0, 0, document.Width, viewport),
            new(0, selectionTop, document.Width, selectionHeight), preferSelectedContent: true);
        Bitmap Capture()
        {
            lock (gate)
            {
                using var surrounding = document.Clone(new Rectangle(0, top, document.Width, viewport),
                    PixelFormat.Format32bppPArgb);
                return context.Extract(surrounding);
            }
        }
        var states = new List<ControlledScrollCaptureState>();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var actions = Channel.CreateUnbounded<ScrollCapturePointerAction>();
        actions.Writer.TryWrite(ScrollCapturePointerAction.Click);
        var target = new ScrollCaptureTarget(new IntPtr(1), new IntPtr(1),
            new(0, selectionTop, document.Width, selectionHeight), false);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var result = await ScrollCaptureService.CaptureControlledCoreAsync(target, completion.Task, actions.Reader,
            options: ScrollCaptureOptions.Default with { MaximumFrames = 300 },
            initialFrame: Capture(),
            stateChanged: state =>
            {
                states.Add(state);
                if (state == ControlledScrollCaptureState.BottomReached)
                    actions.Writer.TryWrite(ScrollCapturePointerAction.DoubleClick);
                if (state is ControlledScrollCaptureState.TopReached or ControlledScrollCaptureState.FinalTopReached or
                    ControlledScrollCaptureState.PausedDown or ControlledScrollCaptureState.PausedUp)
                    completion.TrySetResult();
            },
            driverFactory: inputTarget => new ControlledScrollDriver(inputTarget, (_, delta) =>
            {
                lock (gate) top = Math.Clamp(top - delta / 2, 0, document.Height - viewport);
                return true;
            }, _ => Capture()), cancellationToken: timeout.Token);
        Assert.True(result.IsSuccess, $"{result.ErrorMessage}; {string.Join(", ", states)}");
        using var image = Assert.IsType<CapturedImage>(result.Image);
        Assert.DoesNotContain(ControlledScrollCaptureState.PausedDown, states);
        Assert.DoesNotContain(ControlledScrollCaptureState.PausedUp, states);
        Assert.Contains(ControlledScrollCaptureState.BottomReached, states);
        using var expected = document.Clone(new Rectangle(0, selectionTop, document.Width,
            document.Height - viewport + selectionHeight), PixelFormat.Format32bppPArgb);
        ScrollCaptureFixedRegionsTests.AssertPixelsEqual(expected, image.Bitmap);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void SmallSelectionKeepsExactChatRowsAcrossBlankAndReversals(bool automatic, bool fixedBars)
    {
        using var document = CreateDocument();
        const int viewport = 900, selectionTop = 500, selectionHeight = 234;
        var barTop = fixedBars ? 50 : 0;
        var barBottom = fixedBars ? 70 : 0;
        using var context = new ScrollCaptureContext(new(0, 0, document.Width, viewport + barTop + barBottom),
            new(0, selectionTop + barTop, document.Width, selectionHeight), preferSelectedContent: automatic);
        using var manual = ScrollCaptureComposer.CreateForManualCapture();
        using var controlled = new ControlledScrollCaptureComposer();
        var end = document.Height - viewport;
        var initialTop = end / 2;
        using var initial = Capture(initialTop);
        if (automatic) controlled.Initialize(initial, ScrollCaptureOptions.Default);
        else manual.TryAddFrame(initial, ScrollCaptureOptions.Default, out _);
        var positions = Steps(initialTop, end).Concat(Steps(end, 0)).Concat(Steps(0, end))
            .Concat(Enumerable.Repeat(end, 15)).ToArray();
        var previous = initialTop;
        foreach (var top in positions)
        {
            using var frame = Capture(top);
            if (automatic)
            {
                if (top < initialTop)
                {
                    controlled.BeginUpwardExtension(initial, ScrollCaptureOptions.Default);
                    controlled.TryAddUp(frame, ScrollCaptureOptions.Default, expectedRows: 5);
                }
                else controlled.TryAddDown(frame, ScrollCaptureOptions.Default, expectedRows: 5);
                Assert.True(controlled.LastRejectReason is null,
                    $"Context={ScrollCapturePosition.Read(frame)?.Rows}, top={top}, previous={previous}: {controlled.LastRejectReason}");
            }
            else
            {
                manual.TryAddFrame(frame, top < previous ? ScrollCaptureDirection.Up : ScrollCaptureDirection.Down,
                    ScrollCaptureOptions.Default, expectedNewRows: 5, lockDirection: true, out _);
                Assert.True(manual.LastRejectReason is null,
                    $"Context={ScrollCapturePosition.Read(frame)?.Rows}, top={top}, previous={previous}: {manual.LastRejectReason}");
                Assert.Equal(top - initialTop, manual.CurrentFrameTop);
            }
            previous = top;
        }
        using var result = automatic ? controlled.Compose() : manual.Compose();
        using var expected = document.Clone(new Rectangle(0, selectionTop, document.Width, end + selectionHeight),
            PixelFormat.Format32bppPArgb);
        ScrollCaptureFixedRegionsTests.AssertPixelsEqual(expected, result);
        if (!automatic) Assert.False(manual.CreateLivePreviewState(200, 1000).HasEstimatedBlankSpacing);

        Bitmap Capture(int top)
        {
            using var surrounding = ScrollCaptureFixedRegionsTests.CreateFrame(document, top, viewport, barTop, barBottom);
            return context.Extract(surrounding);
        }
    }

    [Fact]
    public void IdenticalBlankSelectionStillQueuesMeasuredMotionWithoutWheelEvents()
    {
        using var document = CreateDocument();
        using var context = new ScrollCaptureContext(new(0, 0, document.Width, 900), new(0, 500, document.Width, 160));
        using var firstSurrounding = document.Clone(new Rectangle(0, 0, document.Width, 900), PixelFormat.Format32bppPArgb);
        using var nextSurrounding = document.Clone(new Rectangle(0, 47, document.Width, 900), PixelFormat.Format32bppPArgb);
        using var first = context.Extract(firstSurrounding);
        using var next = context.Extract(nextSurrounding);
        using var gate = new CapturedFrameGate();
        gate.Accept(first);
        Assert.Equal(47, ScrollCapturePosition.Difference(first, next));
        ScrollCaptureFixedRegionsTests.AssertPixelsEqual(first, next);
        Assert.True(gate.HasChanged(next));
        gate.AcceptPending();
        Assert.False(gate.HasChanged(next));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnverifiedContextCannotFallBackToGuessedBlankSpacing(bool automatic)
    {
        using var frame = new Bitmap(180, 160, PixelFormat.Format32bppPArgb);
        var session = new object();
        frame.Tag = new ScrollCapturePosition(session, 0);
        using var manual = ScrollCaptureComposer.CreateForManualCapture();
        using var controlled = new ControlledScrollCaptureComposer();
        if (automatic) controlled.Initialize(frame, ScrollCaptureOptions.Default);
        else manual.TryAddFrame(frame, ScrollCaptureOptions.Default, out _);
        frame.Tag = new ScrollCapturePosition(session, null);
        for (var i = 0; i < 10; i++)
        {
            if (automatic)
            {
                Assert.False(controlled.TryAddDown(frame, ScrollCaptureOptions.Default, 80));
                Assert.Equal(160, controlled.OutputHeight);
                Assert.Equal("automatic-context-unverified", controlled.LastRejectReason);
            }
            else
            {
                Assert.False(manual.TryAddFrame(frame, ScrollCaptureDirection.Down,
                    ScrollCaptureOptions.Default, 80, true, out _));
                Assert.Equal(0, manual.CurrentFrameTop);
                Assert.Equal("manual-context-unverified", manual.LastRejectReason);
            }
        }
    }

    private static IEnumerable<int> Steps(int start, int end)
    {
        int[] steps = [31, 59, 13, 47, 1];
        var index = 0;
        yield return start;
        while (start != end)
        {
            start += Math.Sign(end - start) * Math.Min(Math.Abs(end - start), steps[index++ % steps.Length]);
            yield return start;
        }
    }

    private static Bitmap CreateDocument()
    {
        // A locally supplied reference can replay the same assertions without
        // checking any user's conversation image into the repository.
        var replay = Environment.GetEnvironmentVariable("SNAPCUT_SCROLL_REPLAY_IMAGE");
        if (!string.IsNullOrEmpty(replay)) return new Bitmap(replay);
        var document = new Bitmap(492, 2000, PixelFormat.Format32bppPArgb);
        using var graphics = Graphics.FromImage(document);
        graphics.Clear(Color.FromArgb(31, 31, 31));
        using var bubble = new SolidBrush(Color.FromArgb(44, 202, 141));
        using var ink = new SolidBrush(Color.Black);
        using var font = new Font("Segoe UI", 13);
        using var avatar = new Bitmap(45, 45);
        var random = new Random(58934);
        for (var y = 0; y < 45; y++)
        for (var x = 0; x < 45; x++)
            avatar.SetPixel(x, y, Color.FromArgb(random.Next(256), random.Next(256), random.Next(256)));
        foreach (var y in new[] { 44, 116, 378, 959, 1081, 1343, 1414, 1700, 1850 })
        {
            graphics.DrawImageUnscaled(avatar, 432, y);
            if (y == 378) graphics.DrawImage(avatar, new Rectangle(314, y, 100, 100));
            else
            {
                graphics.FillRectangle(bubble, 104, y, 310, 45);
                graphics.DrawString($"Chat row {y}: visible text", font, ink, 116, y + 10);
            }
        }
        return document;
    }
}
