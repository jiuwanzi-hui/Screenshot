using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Screenshot.App.Capture;

namespace Screenshot.App.Tests;

public sealed class ScrollCaptureFixedRegionsTests
{
    [Theory]
    [InlineData(ScrollCaptureDirection.Up, 760)]
    [InlineData(ScrollCaptureDirection.Down, 840)]
    public void AutomaticReturnCrossingMeasuresBodyMovementBehindLargeFixedBars(ScrollCaptureDirection direction, int position)
    {
        using var document = CreateDocument(240, 2000);
        using var initial = CreateFrame(document, 800, 180, 160, 140);
        using var crossed = CreateFrame(document, position, 180, 160, 140);
        var match = ScrollCaptureService.FindControlledInitialOverlap(initial, crossed, direction, 40,
            ScrollCaptureOptions.Default, 160, 140);
        Assert.NotNull(match);
        Assert.Equal(40, initial.Height - match.OverlapRows);
    }

    [Fact]
    public void AutomaticReturnFingerprintIgnoresChangedControlsOutsideTheBody()
    {
        using var document = CreateDocument(240, 2000);
        using var initial = CreateFrame(document, 800, 180, 160, 140);
        using var changed = (Bitmap)initial.Clone();
        using (var graphics = Graphics.FromImage(changed))
        {
            graphics.FillRectangle(Brushes.Blue, 0, 0, changed.Width, 160);
            graphics.FillRectangle(Brushes.White, 0, 340, changed.Width, 140);
        }
        var original = AutomaticViewportFingerprint.Create(initial, 160, 140);
        var current = AutomaticViewportFingerprint.Create(changed, 160, 140);
        Assert.Equal(180, current.SourceHeight);
        Assert.True(original.IsSimilarTo(current));
        using var moved = CreateFrame(document, 880, 180, 160, 140);
        Assert.False(original.IsSimilarTo(AutomaticViewportFingerprint.Create(moved, 160, 140)));
    }

    [Theory]
    [InlineData(false, 48, 0)]
    [InlineData(false, 0, 96)]
    [InlineData(false, 48, 110)]
    [InlineData(true, 48, 0)]
    [InlineData(true, 0, 96)]
    [InlineData(true, 48, 110)]
    public void FixedBarsStayAtTheTwoEndsOfTheImageAndLivePreview(bool automatic, int topRows, int bottomRows)
    {
        const int bodyHeight = 320;
        using var document = CreateDocument(240, 2000);
        using var initial = CreateFrame(document, 800, bodyHeight, topRows, bottomRows);
        using var manual = ScrollCaptureComposer.CreateForManualCapture();
        using var controlled = new ControlledScrollCaptureComposer();
        if (automatic) controlled.Initialize(initial, ScrollCaptureOptions.Default);
        else Assert.True(manual.TryAddFrame(initial, ScrollCaptureOptions.Default, out _));
        var min = 800;
        var max = 800;

        foreach (var position in new[] { 880, 960, 1040, 800, 720, 640, 560 })
        {
            if (automatic && position == 800)
            {
                controlled.MarkDownBoundaryReached();
                controlled.BeginUpwardExtension(initial, ScrollCaptureOptions.Default);
                continue;
            }
            using var frame = CreateFrame(document, position, bodyHeight, topRows, bottomRows);
            if (automatic)
                Assert.True(position > 800
                    ? controlled.TryAddDown(frame, ScrollCaptureOptions.Default, expectedRows: 80)
                    : controlled.TryAddUp(frame, ScrollCaptureOptions.Default, expectedRows: 80), controlled.LastRejectReason);
            else
            {
                Assert.True(manual.TryAddFrame(frame, ScrollCaptureOptions.Default, out _) || position == 800,
                    manual.LastRejectReason);
                Assert.Equal(position - 800, manual.CurrentFrameTop);
            }
            min = Math.Min(min, position);
            max = Math.Max(max, position);
            using var expected = CreateFrame(document, min, max - min + bodyHeight, topRows, bottomRows);
            using var result = automatic ? controlled.Compose() : manual.Compose();
            using var preview = automatic ? controlled.ComposeLivePreview(240, 4000) : manual.ComposeLivePreview(240, 4000);
            AssertPixelsEqual(expected, result);
            AssertPixelsEqual(expected, preview);
            Assert.Equal(topRows, automatic ? controlled.FixedTopRows : manual.FixedTopRows);
            Assert.Equal(bottomRows, automatic ? controlled.FixedBottomRows : manual.FixedBottomRows);
        }
    }

    [Fact]
    public void ManualReversalsAcrossBothOldEdgesDoNotRepeatBarsOrLoseContent()
    {
        using var document = CreateDocument(240, 2000);
        using var composer = ScrollCaptureComposer.CreateForManualCapture();
        foreach (var position in new[] { 800, 880, 960, 1040, 900, 760, 620, 500, 640, 780, 920, 1060, 1200 })
        {
            using var frame = CreateFrame(document, position, 320, 52, 88);
            composer.TryAddFrame(frame, ScrollCaptureOptions.Default, out _);
            Assert.Equal(position - 800, composer.CurrentFrameTop);
            Assert.NotNull(composer.LastFrameMovementRows);
        }
        using var expected = CreateFrame(document, 500, 1020, 52, 88);
        using var actual = composer.Compose();
        AssertPixelsEqual(expected, actual);
        using var boundary = CreateFrame(document, 1200, 320, 52, 88);
        Assert.True(composer.TryMarkBoundaryReached(boundary, ScrollCaptureDirection.Down));
        using var preview = composer.ComposeLivePreview(120, 250);
        Assert.InRange(preview.Width, 1, 120);
        Assert.InRange(preview.Height, 1, 250);
        Assert.Equal((int)Math.Round(actual.Width * 250d / actual.Height), preview.Width);
        Assert.Equal(250, preview.Height);
    }

    [Fact]
    public void AutomaticCaptureCanStartUpwardBeforeDetectingTheFixedBars()
    {
        using var document = CreateDocument(240, 2000);
        using var initial = CreateFrame(document, 800, 320, 45, 75);
        using var composer = new ControlledScrollCaptureComposer();
        composer.Initialize(initial, ScrollCaptureOptions.Default);
        composer.BeginUpwardExtension(initial, ScrollCaptureOptions.Default);
        foreach (var position in new[] { 720, 640, 560 })
        {
            using var frame = CreateFrame(document, position, 320, 45, 75);
            Assert.True(composer.TryAddUp(frame, ScrollCaptureOptions.Default, 80), composer.LastRejectReason);
        }
        foreach (var position in new[] { 880, 960 })
        {
            using var frame = CreateFrame(document, position, 320, 45, 75);
            Assert.True(composer.TryAddDown(frame, ScrollCaptureOptions.Default, 80), composer.LastRejectReason);
        }
        using var expected = CreateFrame(document, 560, 720, 45, 75);
        using var actual = composer.Compose();
        AssertPixelsEqual(expected, actual);
        Assert.Equal(160, composer.GetTravelFromInitial(ScrollCaptureDirection.Up));
        Assert.Equal(240, composer.GetTravelFromInitial(ScrollCaptureDirection.Down));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ChatWhitespaceBelowAHeaderIsPreserved(bool automatic)
    {
        using var document = CreateDocument(440, 2000, sparseChat: true);
        using var initial = CreateFrame(document, 840, 400, 56, 80);
        using var manual = ScrollCaptureComposer.CreateForManualCapture();
        using var controlled = new ControlledScrollCaptureComposer();
        if (automatic) controlled.Initialize(initial, ScrollCaptureOptions.Default);
        else manual.TryAddFrame(initial, ScrollCaptureOptions.Default, out _);
        foreach (var position in new[] { 890, 940, 990 })
        {
            using var frame = CreateFrame(document, position, 400, 56, 80);
            Assert.True(automatic
                ? controlled.TryAddDown(frame, ScrollCaptureOptions.Default, 50)
                : manual.TryAddFrame(frame, ScrollCaptureOptions.Default, out _),
                automatic ? controlled.LastRejectReason : manual.LastRejectReason);
        }
        using var expected = CreateFrame(document, 840, 550, 56, 80);
        using var actual = automatic ? controlled.Compose() : manual.Compose();
        Assert.Equal(56, automatic ? controlled.FixedTopRows : manual.FixedTopRows);
        Assert.Equal(80, automatic ? controlled.FixedBottomRows : manual.FixedBottomRows);
        AssertPixelsEqual(expected, actual);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnUnchangedFrameDoesNotEstablishFixedBars(bool automatic)
    {
        using var document = CreateDocument(240, 2000);
        using var initial = CreateFrame(document, 800, 320, 48, 96);
        using var manual = ScrollCaptureComposer.CreateForManualCapture();
        using var controlled = new ControlledScrollCaptureComposer();
        if (automatic) controlled.Initialize(initial, ScrollCaptureOptions.Default);
        else manual.TryAddFrame(initial, ScrollCaptureOptions.Default, out _);
        for (var i = 0; i < 3; i++)
            Assert.False(automatic
                ? controlled.TryAddDown(initial, ScrollCaptureOptions.Default, 80)
                : manual.TryAddFrame(initial, ScrollCaptureOptions.Default, out _));
        Assert.Equal(0, automatic ? controlled.FixedTopRows : manual.FixedTopRows);
        Assert.Equal(0, automatic ? controlled.FixedBottomRows : manual.FixedBottomRows);
        using var next = CreateFrame(document, 880, 320, 48, 96);
        Assert.True(automatic
            ? controlled.TryAddDown(next, ScrollCaptureOptions.Default, 80)
            : manual.TryAddFrame(next, ScrollCaptureOptions.Default, out _));
        using var expected = CreateFrame(document, 800, 400, 48, 96);
        using var actual = automatic ? controlled.Compose() : manual.Compose();
        AssertPixelsEqual(expected, actual);
    }

    [Fact]
    public void PlainScrollingWhitespaceIsNotAFixedBar()
    {
        using var document = CreateDocument(440, 2000, sparseChat: true);
        using var composer = ScrollCaptureComposer.CreateForManualCapture();
        foreach (var position in new[] { 840, 890, 940, 990 })
        {
            using var frame = CreateFrame(document, position, 400, 0, 0);
            Assert.True(composer.TryAddFrame(frame, ScrollCaptureOptions.Default, out _));
            Assert.Equal(0, composer.FixedTopRows);
            Assert.Equal(0, composer.FixedBottomRows);
        }
        using var expected = CreateFrame(document, 840, 550, 0, 0);
        using var actual = composer.Compose();
        AssertPixelsEqual(expected, actual);
    }

    private static Bitmap CreateDocument(int width, int height, bool sparseChat = false)
    {
        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
        var pixels = new int[width * height];
        var random = new Random(90427);
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                pixels[y * width + x] = sparseChat && (y % 180 < 90 || x < width / 2 || x > width - 28)
                    ? Color.FromArgb(30, 30, 30).ToArgb()
                    : Color.FromArgb(random.Next(40, 240), random.Next(40, 240), random.Next(40, 240)).ToArgb();
        var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
        try { Marshal.Copy(pixels, 0, data.Scan0, pixels.Length); }
        finally { bitmap.UnlockBits(data); }
        return bitmap;
    }

    internal static Bitmap CreateFrame(Bitmap document, int documentTop, int bodyHeight, int topRows, int bottomRows)
    {
        var bitmap = new Bitmap(document.Width, topRows + bodyHeight + bottomRows, PixelFormat.Format32bppPArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.CompositingMode = CompositingMode.SourceCopy;
        graphics.DrawImage(document, new Rectangle(0, topRows, document.Width, bodyHeight),
            new Rectangle(0, documentTop, document.Width, bodyHeight), GraphicsUnit.Pixel);
        using var background = new SolidBrush(Color.FromArgb(34, 34, 34));
        if (topRows > 0)
        {
            graphics.FillRectangle(background, 0, 0, document.Width, topRows);
            graphics.FillRectangle(Brushes.White, 12, 10, 60, 12);
            graphics.FillRectangle(Brushes.Gray, document.Width - 40, 10, 12, 12);
        }
        if (bottomRows > 0)
        {
            graphics.FillRectangle(background, 0, topRows + bodyHeight, document.Width, bottomRows);
            graphics.FillRectangle(Brushes.Gray, 18, topRows + bodyHeight + 5, document.Width - 50, Math.Min(12, bottomRows - 5));
        }
        return bitmap;
    }

    internal static void AssertPixelsEqual(Bitmap expected, Bitmap actual)
    {
        Assert.Equal(expected.Size, actual.Size);
        var bounds = new Rectangle(0, 0, expected.Width, expected.Height);
        var a = expected.LockBits(bounds, ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        var b = actual.LockBits(bounds, ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        try
        {
            var expectedRow = new int[expected.Width];
            var actualRow = new int[expected.Width];
            for (var y = 0; y < expected.Height; y++)
            {
                Marshal.Copy(IntPtr.Add(a.Scan0, y * a.Stride), expectedRow, 0, expected.Width);
                Marshal.Copy(IntPtr.Add(b.Scan0, y * b.Stride), actualRow, 0, expected.Width);
                if (!expectedRow.AsSpan().SequenceEqual(actualRow))
                {
                    var x = Enumerable.Range(0, expected.Width).First(i => expectedRow[i] != actualRow[i]);
                    Assert.Fail($"Pixel mismatch at ({x}, {y}): expected {expectedRow[x]:X8}, actual {actualRow[x]:X8}.");
                }
            }
        }
        finally { expected.UnlockBits(a); actual.UnlockBits(b); }
    }
}
