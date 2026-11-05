using System.Drawing;
using System.Drawing.Imaging;
using Screenshot.App.Capture;

namespace Screenshot.App.Tests;

public sealed class ManualBlankScrollTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public void CrossingLongBlankInBothDirectionsKeepsLaterContent(bool dark, bool fixedBars, bool beginUpward)
    {
        using var document = CreateDocument(dark);
        using var composer = ScrollCaptureComposer.CreateForManualCapture();
        var topBar = fixedBars ? 24 : 0;
        var bottomBar = fixedBars ? 32 : 0;
        var initial = beginUpward ? 1400 : 0;
        var last = initial;
        var positions = Enumerable.Range(0, 36).Select(i => i * 40)
            .Concat(Enumerable.Range(0, 36).Select(i => 1400 - i * 40))
            .Concat(Enumerable.Range(0, 36).Select(i => i * 40));
        foreach (var position in positions)
        {
            var top = beginUpward ? 1400 - position : position;
            using var frame = ScrollCaptureFixedRegionsTests.CreateFrame(document, top, 160, topBar, bottomBar);
            composer.TryAddFrame(frame, top < last ? ScrollCaptureDirection.Up : ScrollCaptureDirection.Down,
                ScrollCaptureOptions.Default, expectedNewRows: 40, lockDirection: true, out _);
            Assert.NotNull(composer.LastFrameMovementRows);
            Assert.Equal(top - initial, composer.CurrentFrameTop);
            last = top;
        }
        using var expected = ScrollCaptureFixedRegionsTests.CreateFrame(document, 0, 1560, topBar, bottomBar);
        using var actual = composer.Compose();
        ScrollCaptureFixedRegionsTests.AssertPixelsEqual(expected, actual);
        Assert.True(composer.CreateLivePreviewState(180, 2000).HasEstimatedBlankSpacing);
    }

    [Fact]
    public void AOnePixelContentEdgeAfterBlankCanAnchorTheFollowingFrame()
    {
        using var document = CreateDocument(dark: true);
        using var composer = ScrollCaptureComposer.CreateForManualCapture();
        int[] positions = [0, 40, 80, 120, 160, 200, 240, 360, 480, 600, 720, 840,
            960, 1040, 1041, 1081, 1121, 1161, 1201, 1241];
        var last = 0;
        foreach (var top in positions)
        {
            using var frame = document.Clone(new Rectangle(0, top, 180, 160), PixelFormat.Format32bppPArgb);
            composer.TryAddFrame(frame, ScrollCaptureDirection.Down, ScrollCaptureOptions.Default,
                expectedNewRows: top - last, lockDirection: true, out _);
            Assert.True(composer.LastFrameMovementRows is not null, $"Lost at {top}: {composer.LastRejectReason}");
            Assert.Equal(top, composer.CurrentFrameTop);
            last = top;
        }
        using var expected = document.Clone(new Rectangle(0, 0, 180, 1401), PixelFormat.Format32bppPArgb);
        using var result = composer.Compose();
        ScrollCaptureFixedRegionsTests.AssertPixelsEqual(expected, result);
    }

    [Fact]
    public void BlankAtBoundaryDoesNotGrowResultOrRetainEveryRepeatedFrame()
    {
        using var blank = new Bitmap(180, 160, PixelFormat.Format32bppPArgb);
        using (var graphics = Graphics.FromImage(blank)) graphics.Clear(Color.White);
        using var composer = ScrollCaptureComposer.CreateForManualCapture();
        composer.TryAddFrame(blank, ScrollCaptureOptions.Default, out _);
        for (var i = 0; i < 100; i++)
        {
            Assert.False(composer.TryAddFrame(blank, ScrollCaptureDirection.Down,
                ScrollCaptureOptions.Default, expectedNewRows: 40, lockDirection: true, out _));
            Assert.Equal(1, composer.FrameCount);
            Assert.Equal(160, composer.OutputHeight);
        }
        var position = composer.CurrentFrameTop;
        composer.TryAddFrame(blank, ScrollCaptureDirection.Down, ScrollCaptureOptions.Default, out _);
        Assert.Equal(position, composer.CurrentFrameTop);
        using var result = composer.Compose();
        ScrollCaptureFixedRegionsTests.AssertPixelsEqual(blank, result);
    }

    [Fact]
    public void ReturningAfterExtraWheelTurnsAtBlankBoundaryDoesNotDuplicateOldText()
    {
        using var document = CreateDocument(dark: false);
        using var composer = ScrollCaptureComposer.CreateForManualCapture();
        foreach (var top in Enumerable.Range(0, 7).Select(index => index * 40).Concat(Enumerable.Repeat(240, 30)))
        {
            using var frame = document.Clone(new Rectangle(0, top, 180, 160), PixelFormat.Format32bppPArgb);
            composer.TryAddFrame(frame, ScrollCaptureDirection.Down, ScrollCaptureOptions.Default,
                expectedNewRows: 40, lockDirection: true, out _);
        }
        var height = composer.OutputHeight;
        using var returned = document.Clone(new Rectangle(0, 200, 180, 160), PixelFormat.Format32bppPArgb);
        composer.TryAddFrame(returned, ScrollCaptureDirection.Up, ScrollCaptureOptions.Default,
            expectedNewRows: 40, lockDirection: true, out _);
        Assert.Equal(200, composer.CurrentFrameTop);
        Assert.Equal(height, composer.OutputHeight);
    }

    [Fact]
    public void BlankSamplingRetainsWheelTravelButDoesNotTreatIdleAsMovement()
    {
        using var blank = new Bitmap(180, 160, PixelFormat.Format32bppPArgb);
        using (var graphics = Graphics.FromImage(blank)) graphics.Clear(Color.FromArgb(30, 30, 30));
        using var gate = new CapturedFrameGate();
        gate.Accept(blank);
        Assert.False(gate.HasChanged(blank));
        Assert.True(gate.ShouldRetainBlankMotion(true, 0, 0));
        Assert.False(gate.ShouldRetainBlankMotion(false, 0, 0));
        blank.SetPixel(80, 80, Color.White);
        gate.HasChanged(blank);
        Assert.False(gate.ShouldRetainBlankMotion(true, 0, 0));
    }

    private static Bitmap CreateDocument(bool dark)
    {
        var document = new Bitmap(180, 2000, PixelFormat.Format32bppPArgb);
        using (var graphics = Graphics.FromImage(document))
            graphics.Clear(dark ? Color.FromArgb(30, 30, 30) : Color.White);
        var random = new Random(5517);
        for (var y = 0; y < document.Height; y++)
        {
            if (y is >= 240 and < 1200) continue;
            for (var x = 0; x < document.Width; x++)
                document.SetPixel(x, y, Color.FromArgb(random.Next(256), random.Next(256), random.Next(256)));
        }
        return document;
    }
}
