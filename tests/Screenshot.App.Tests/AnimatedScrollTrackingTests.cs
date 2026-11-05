using System.Drawing;
using System.Drawing.Imaging;
using System.Threading.Channels;
using Screenshot.App.Capture;

namespace Screenshot.App.Tests;

public sealed class AnimatedScrollTrackingTests
{
    [Fact]
    public void AliasedChatTextKeepsUniqueMotionDespiteAnimatedSticker()
    {
        using var stream = typeof(AnimatedScrollTrackingTests).Assembly.GetManifestResourceStream("Screenshot.App.Tests.Fixtures.aliased-chat.png")!;
        using var document = new Bitmap(stream);
        using var initial = Frame(document, 900, 0);
        using var next = Frame(document, 930, 1);
        using var oldPixels = new ManualScrollFrame(initial);
        using var newPixels = new ManualScrollFrame(next);
        var match = oldPixels.Locate(newPixels, 32, 50, 0);
        Assert.NotNull(match);
        Assert.Equal(30, match.Value.Shift);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AutomaticAnimatedChatReturnsAndExtendsWithoutPausing(bool aliased)
    {
        using var document = CreateChat(720, 2400, aliased);
        var top = 900;
        var phase = 0;
        var gate = new object();
        var requestedReturn = false;
        var inputBodyChanged = false;
        var addedAbove = 0;
        var states = new List<ControlledScrollCaptureState>();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var actions = Channel.CreateUnbounded<ScrollCapturePointerAction>();
        actions.Writer.TryWrite(ScrollCapturePointerAction.Click);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var target = new ScrollCaptureTarget(new(1), new(1), new(0, 0, 720, 470), false);
        var result = await ScrollCaptureService.CaptureControlledCoreAsync(target, completion.Task, actions.Reader,
            stateChanged: state =>
            {
                states.Add(state);
                if (state is ControlledScrollCaptureState.PausedUp or ControlledScrollCaptureState.PausedDown)
                    completion.TrySetResult();
            },
            previewChanged: preview =>
            {
                addedAbove = preview.AddedAboveFrameCount;
                if (!requestedReturn && preview.AddedBelowFrameCount >= 5)
                {
                    requestedReturn = true;
                    actions.Writer.TryWrite(ScrollCapturePointerAction.DoubleClick);
                }
                if (preview.AddedAboveFrameCount >= 5) completion.TrySetResult();
            },
            initialFrame: Frame(document, top, phase++),
            driverFactory: captureTarget => new ControlledScrollDriver(captureTarget, (routingTarget, delta) =>
            {
                lock (gate)
                {
                    inputBodyChanged |= routingTarget.ScrollInputRegionProvider?.Invoke().Y == 50;
                    top = Math.Clamp(top - delta / 2, 0, document.Height - 420);
                }
                return true;
            }, _ =>
            {
                lock (gate) return Frame(document, top, phase++);
            }), cancellationToken: timeout.Token);
        Assert.True(result.IsSuccess, $"{result.ErrorMessage}; {string.Join(",", states)}");
        using var captured = Assert.IsType<CapturedImage>(result.Image);
        Assert.True(requestedReturn);
        Assert.True(inputBodyChanged);
        Assert.Contains(ControlledScrollCaptureState.ScrollingUp, states);
        Assert.DoesNotContain(ControlledScrollCaptureState.PausedUp, states);
        Assert.DoesNotContain(ControlledScrollCaptureState.PausedDown, states);
        Assert.True(top < 900);
        Assert.True(addedAbove >= 5);
    }

    [Theory]
    [InlineData(ScrollCaptureDirection.Up, 870, false)]
    [InlineData(ScrollCaptureDirection.Up, 870, true)]
    [InlineData(ScrollCaptureDirection.Down, 930, false)]
    [InlineData(ScrollCaptureDirection.Down, 930, true)]
    public void AutomaticReturnFindsTheInitialCrossingDespiteAnimation(ScrollCaptureDirection direction, int top, bool aliased)
    {
        using var document = CreateChat(720, 2400, aliased);
        using var initial = Frame(document, 900, 0);
        using var returned = Frame(document, top, 1);
        var match = ScrollCaptureService.FindControlledInitialOverlap(initial, returned,
            direction, 30, ScrollCaptureOptions.Default, fixedTopRows: 50);
        Assert.NotNull(match);
        Assert.Equal(30, returned.Height - match.OverlapRows);
        var opposite = direction == ScrollCaptureDirection.Up ? ScrollCaptureDirection.Down : ScrollCaptureDirection.Up;
        Assert.Null(ScrollCaptureService.FindControlledInitialOverlap(initial, returned,
            opposite, 30, ScrollCaptureOptions.Default, fixedTopRows: 50));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void AutomaticCaptureTracksAnimatedChatWithAFixedTitle(bool upward, bool aliased)
    {
        using var document = CreateChat(720, 2400, aliased);
        using var composer = new ControlledScrollCaptureComposer();
        using var initial = Frame(document, 900, 0);
        composer.Initialize(initial, ScrollCaptureOptions.Default);
        if (upward) composer.BeginUpwardExtension(initial, ScrollCaptureOptions.Default);
        for (var i = 1; i <= 8; i++)
        {
            using var frame = Frame(document, 900 + (upward ? -1 : 1) * i * 30, i);
            var added = upward
                ? composer.TryAddUp(frame, ScrollCaptureOptions.Default, 30)
                : composer.TryAddDown(frame, ScrollCaptureOptions.Default, 30);
            Assert.True(added, $"{i}: {composer.LastRejectReason}");
            Assert.Equal(470 + i * 30, composer.OutputHeight);
        }
        Assert.Equal(50, composer.FixedTopRows);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnAnimatedStickerDoesNotStopUpwardCaptureOrCreateABottomStrip(bool aliased)
    {
        using var document = CreateChat(720, 2400, aliased);
        using var composer = ScrollCaptureComposer.CreateForManualCapture();
        var sequence = 0;
        foreach (var top in new[] { 900, 870, 840, 800, 750, 710, 680, 620 })
        {
            using var frame = Frame(document, top, sequence++);
            Assert.True(composer.TryAddFrame(frame, ScrollCaptureDirection.Up, ScrollCaptureOptions.Default, out _),
                $"{top}: {composer.LastRejectReason}");
            Assert.Equal(top - 900, composer.CurrentFrameTop);
            Assert.Equal(0, composer.AddedBelowFrameCount);
            Assert.Equal(470 + 900 - top, composer.OutputHeight);
        }
        Assert.Equal(50, composer.FixedTopRows);
    }

    [Fact]
    public void MatchingAvatarsCannotOverrideDifferentMessageText()
    {
        using var first = CreateAvatarFrame(offset: 0, textSeed: 1);
        using var different = CreateAvatarFrame(offset: 67, textSeed: 2);
        using var previous = new ManualScrollFrame(first);
        using var current = new ManualScrollFrame(different);
        Assert.Null(previous.Locate(current, 32));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StationaryAnimatedContentDoesNotGrowTheScreenshot(bool aliased)
    {
        using var document = CreateChat(720, 2400, aliased);
        using var composer = ScrollCaptureComposer.CreateForManualCapture();
        using (var initial = Frame(document, 900, 0)) composer.TryAddFrame(initial, ScrollCaptureOptions.Default, out _);
        using (var moved = Frame(document, 850, 1)) Assert.True(composer.TryAddFrame(moved, ScrollCaptureOptions.Default, out _));
        for (var i = 2; i < 8; i++)
        {
            using var frame = Frame(document, 850, i);
            Assert.False(composer.TryAddFrame(frame, ScrollCaptureOptions.Default, out _));
            Assert.Equal(-50, composer.CurrentFrameTop);
            Assert.Equal(520, composer.OutputHeight);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnimatedChatReversesAcrossBothSavedEdgesWithoutLosingStaticPixels(bool aliased)
    {
        using var document = CreateChat(720, 2400, aliased);
        using var composer = ScrollCaptureComposer.CreateForManualCapture();
        var minimum = 900;
        var maximum = 900;
        var phase = 0;
        foreach (var top in new[] { 900, 930, 990, 1080, 1160, 1090, 1000, 910, 820,
                     730, 640, 550, 620, 740, 860, 980, 1100, 1220, 1340 })
        {
            using var frame = Frame(document, top, phase++);
            var expanding = top < minimum || top > maximum || phase == 1;
            Assert.Equal(expanding, composer.TryAddFrame(frame, ScrollCaptureOptions.Default, out _));
            Assert.Equal(top - 900, composer.CurrentFrameTop);
            minimum = Math.Min(top, minimum);
            maximum = Math.Max(top, maximum);
            using var preview = composer.ComposeLivePreview(720, 4000);
            Assert.Equal(maximum - minimum + 470, preview.Height);
        }
        using var result = composer.Compose();
        using var expected = ScrollCaptureFixedRegionsTests.CreateFrame(document, minimum,
            maximum - minimum + 420, 50, 0);
        // An animated sticker can contain pixels from different capture times.
        // All surrounding document pixels must still be present exactly once.
        var animation = new Rectangle(315, 1050 - minimum + 50, 184, 116);
        using (var graphics = Graphics.FromImage(expected)) graphics.FillRectangle(Brushes.Black, animation);
        using (var graphics = Graphics.FromImage(result)) graphics.FillRectangle(Brushes.Black, animation);
        ScrollCaptureFixedRegionsTests.AssertPixelsEqual(expected, result);
    }

    internal static Bitmap CreateChat(int width, int height, bool aliased = false)
    {
        if (aliased)
        {
            using var stream = typeof(AnimatedScrollTrackingTests).Assembly.GetManifestResourceStream("Screenshot.App.Tests.Fixtures.aliased-chat.png")!;
            using var decoded = new Bitmap(stream);
            return decoded.Clone(new Rectangle(0, 0, width, height), PixelFormat.Format32bppPArgb);
        }
        var result = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
        using var graphics = Graphics.FromImage(result);
        graphics.Clear(Color.FromArgb(30, 30, 30));
        using var font = new Font("Consolas", 15, GraphicsUnit.Pixel);
        for (var y = 0; y + 50 < height; y += 85)
        {
            graphics.FillRectangle(Brushes.DimGray, 15, y + 8, 38, 38);
            graphics.DrawString("A", font, Brushes.White, 24, y + 18);
            graphics.DrawString($"Chat message {y / 85:D3}", font, Brushes.White, 80, y + 16);
            graphics.DrawString($"reply {y * 173:D6}", font, Brushes.LightGreen, width - 235, y + 50);
        }
        return result;
    }

    internal static Bitmap Frame(Bitmap document, int top, int phase)
    {
        var result = ScrollCaptureFixedRegionsTests.CreateFrame(document, top, 420, 50, 0);
        using var graphics = Graphics.FromImage(result);
        // One animated image occupies parts of multiple matching columns.
        // Its document position remains fixed while its pixels change.
        graphics.SetClip(new Rectangle(0, 50, document.Width, 420));
        var y = 1050 - top + 50;
        using var color = new SolidBrush(phase % 2 == 0 ? Color.DarkOrange : Color.CornflowerBlue);
        graphics.FillRectangle(color, 315, y, 180, 115);
        using var pen = new Pen(phase % 2 == 0 ? Color.Yellow : Color.Black, 3);
        for (var x = 318; x < 490; x += 11)
            graphics.DrawLine(pen, x, y, x + 5, y + 110);
        return result;
    }

    private static Bitmap CreateAvatarFrame(int offset, int textSeed)
    {
        var result = new Bitmap(640, 420, PixelFormat.Format32bppPArgb);
        using var graphics = Graphics.FromImage(result);
        graphics.Clear(Color.FromArgb(30, 30, 30));
        using var font = new Font("Consolas", 14, GraphicsUnit.Pixel);
        using var avatar = new Bitmap(40, 40);
        var random = new Random(128);
        for (var y = 0; y < 40; y++)
            for (var x = 0; x < 40; x++)
                avatar.SetPixel(x, y, Color.FromArgb(random.Next(256), random.Next(256), random.Next(256)));
        foreach (var y in new[] { 20, 147, 302 })
            graphics.DrawImageUnscaled(avatar, 12, y + offset);
        for (var y = 20; y < 390; y += 38)
            graphics.DrawString($"{textSeed * 397 + y:X4}", font, Brushes.White, 560, y);
        return result;
    }
}
