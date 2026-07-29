using System.Drawing;
using System.Drawing.Imaging;
using Screenshot.App.Capture;

namespace Screenshot.App.Tests;

public sealed class ControlledScrollCaptureComposerTests
{
    [Fact]
    public void PlacesAboveStartContentBeforeTheDownwardResult()
    {
        using var document = CreateDocument(width: 180, height: 720);
        using var initial = document.Clone(
            new Rectangle(0, 240, 180, 180),
            PixelFormat.Format32bppPArgb);
        var options = ScrollCaptureOptions.Default with
        {
            MinimumOverlapConfidence = 0.90,
        };
        using var composer = new ControlledScrollCaptureComposer();
        composer.Initialize(initial, options);

        foreach (var top in new[] { 280, 320, 360, 400, 440 })
        {
            using var frame = document.Clone(
                new Rectangle(0, top, 180, 180),
                PixelFormat.Format32bppPArgb);
            Assert.True(composer.TryAddDown(frame, options, expectedRows: 40));
        }

        composer.BeginUpwardExtension(initial, options);
        foreach (var top in new[] { 200, 160, 120, 80, 40, 0 })
        {
            using var frame = document.Clone(
                new Rectangle(0, top, 180, 180),
                PixelFormat.Format32bppPArgb);
            Assert.True(composer.TryAddUp(frame, options, expectedRows: 40));
        }

        using var result = composer.Compose();
        Assert.Equal(180, result.Width);
        Assert.Equal(620, result.Height);
        Assert.Equal(document.GetPixel(25, 0), result.GetPixel(25, 0));
        Assert.Equal(document.GetPixel(91, 239), result.GetPixel(91, 239));
        Assert.Equal(document.GetPixel(47, 240), result.GetPixel(47, 240));
        Assert.Equal(document.GetPixel(132, 619), result.GetPixel(132, 619));
    }

    [Fact]
    public void SparseCodeBackgroundStaysExactAcrossMultipleUpwardViewports()
    {
        const int width = 240;
        const int viewportHeight = 180;
        const int initialTop = 300;
        using var document = CreateSparseCodeDocument(width, height: 540);
        using (var graphics = Graphics.FromImage(document))
        using (var background = new SolidBrush(Color.FromArgb(255, 30, 32, 36)))
        {
            // A large empty code block makes same-position sparse sampling look
            // like fixed chrome even though the whole editor is scrolling.
            graphics.FillRectangle(background, 0, 270, width, 75);
        }
        using var initial = document.Clone(
            new Rectangle(0, initialTop, width, viewportHeight),
            PixelFormat.Format32bppPArgb);
        using var firstUp = document.Clone(
            new Rectangle(0, initialTop - 30, width, viewportHeight),
            PixelFormat.Format32bppPArgb);
        Assert.True(
            ImageOverlapMatcher.FindStationaryLeadingRows(
                initial,
                firstUp,
                ScrollCaptureDirection.Up,
                movementRows: 30) > 0,
            "The fixture must reproduce the sparse-background false header.");

        var options = ScrollCaptureOptions.Default with
        {
            MinimumOverlapConfidence = 0.90,
        };
        using var composer = new ControlledScrollCaptureComposer();
        composer.Initialize(initial, options);
        composer.BeginUpwardExtension(initial, options);

        for (var top = initialTop - 30; top >= 0; top -= 30)
        {
            using var frame = document.Clone(
                new Rectangle(0, top, width, viewportHeight),
                PixelFormat.Format32bppPArgb);
            Assert.True(composer.TryAddUp(frame, options, expectedRows: 30));
        }

        using var result = composer.Compose();
        Assert.Equal(initialTop + viewportHeight, result.Height);
        AssertPixelsEqual(document, result, result.Height);
    }

    [Fact]
    public void PreviousMovementPreventsSkippingAPeriodicCodeRow()
    {
        const int width = 180;
        const int viewportHeight = 216;
        const int lineHeight = 27;
        using var document = CreatePeriodicDocument(
            width,
            viewportHeight + 107,
            lineHeight);
        using var previous = document.Clone(
            new Rectangle(0, 54, width, viewportHeight),
            PixelFormat.Format32bppPArgb);
        using var current = document.Clone(
            new Rectangle(0, 107, width, viewportHeight),
            PixelFormat.Format32bppPArgb);

        // The fixed wheel packet count can lag smooth-scroll inertia. On a
        // 27px code-line period, preferring 25px selects the equally exact 26px
        // overlap and silently drops one complete line.
        var laggingInputMatch = ImageOverlapMatcher.FindVerticalOverlap(
            previous,
            current,
            minimumOverlapRows: 24,
            minimumConfidence: 0.96,
            minimumNewRows: 8,
            preferredNewRows: 25);
        Assert.NotNull(laggingInputMatch);
        Assert.Equal(26, viewportHeight - laggingInputMatch.OverlapRows);

        var stableExpectedRows =
            ControlledScrollCaptureComposer.GetPreferredExpectedRows(
                inputExpectedRows: 25,
                recentExpansionRows: [29, 54]);
        Assert.Equal(54, stableExpectedRows);
        var stableMatch = ImageOverlapMatcher.FindVerticalOverlap(
            previous,
            current,
            minimumOverlapRows: 24,
            minimumConfidence: 0.96,
            minimumNewRows: 8,
            preferredNewRows: stableExpectedRows);
        Assert.NotNull(stableMatch);
        Assert.Equal(53, viewportHeight - stableMatch.OverlapRows);

        Assert.Equal(
            85,
            ControlledScrollCaptureComposer.GetPreferredExpectedRows(
                inputExpectedRows: 85,
                recentExpansionRows: [56, 57, 165]));
        Assert.Equal(
            10,
            ControlledScrollCaptureComposer.GetPreferredExpectedRows(
                inputExpectedRows: 10,
                recentExpansionRows: [165]));
    }

    [Fact]
    public void FullBoundaryViewportRepairsACorruptedIncrementalStrip()
    {
        const int width = 180;
        const int viewportHeight = 160;
        const int movementRows = 40;
        using var document = CreateDocument(width, height: 240);
        using var initial = document.Clone(
            new Rectangle(0, 0, width, viewportHeight),
            PixelFormat.Format32bppPArgb);
        using var corrupted = document.Clone(
            new Rectangle(0, movementRows, width, viewportHeight),
            PixelFormat.Format32bppPArgb);
        using (var graphics = Graphics.FromImage(corrupted))
        using (var brush = new SolidBrush(Color.Magenta))
        {
            graphics.FillRectangle(
                brush,
                0,
                viewportHeight - movementRows,
                width,
                movementRows);
        }

        var options = ScrollCaptureOptions.Default with
        {
            MinimumOverlapConfidence = 0.90,
        };
        using var composer = new ScrollCaptureComposer(
            detectStationaryLeadingRows: false);
        Assert.True(composer.TryAddFrame(initial, options, out _));
        Assert.True(composer.TryAddFrame(
            corrupted,
            ScrollCaptureDirection.Down,
            options,
            expectedNewRows: movementRows,
            lockDirection: true,
            out _));

        using var cleanBoundary = document.Clone(
            new Rectangle(0, movementRows, width, viewportHeight),
            PixelFormat.Format32bppPArgb);
        composer.RefreshBoundaryViewport(
            cleanBoundary,
            ScrollCaptureDirection.Down);

        using var result = composer.Compose();
        AssertPixelsEqual(document, result, viewportHeight + movementRows);
    }

    [Fact]
    public void BoundaryRefreshNeverOverwritesTheInitialViewport()
    {
        const int width = 180;
        const int viewportHeight = 160;
        const int movementRows = 20;
        using var document = CreateDocument(width, height: 220);
        using var initial = document.Clone(
            new Rectangle(0, 0, width, viewportHeight),
            PixelFormat.Format32bppPArgb);
        using var nextFrame = document.Clone(
            new Rectangle(0, movementRows, width, viewportHeight),
            PixelFormat.Format32bppPArgb);
        using var verticallyMisalignedRefresh = document.Clone(
            new Rectangle(0, movementRows + 4, width, viewportHeight),
            PixelFormat.Format32bppPArgb);

        var options = ScrollCaptureOptions.Default with
        {
            MinimumOverlapConfidence = 0.90,
        };
        using var composer = new ScrollCaptureComposer(
            detectStationaryLeadingRows: false);
        Assert.True(composer.TryAddFrame(initial, options, out _));
        Assert.True(composer.TryAddFrame(
            nextFrame,
            ScrollCaptureDirection.Down,
            options,
            expectedNewRows: movementRows,
            lockDirection: true,
            out _));

        composer.RefreshBoundaryViewport(
            verticallyMisalignedRefresh,
            ScrollCaptureDirection.Down);

        using var result = composer.Compose();
        for (var y = 0; y < viewportHeight; y++)
        {
            for (var x = 0; x < width; x++)
            {
                Assert.Equal(
                    initial.GetPixel(x, y).ToArgb(),
                    result.GetPixel(x, y).ToArgb());
            }
        }
    }

    [Fact]
    public void UpwardBoundaryRefreshOnlyReplacesTheNewlyAddedStrip()
    {
        const int width = 180;
        const int viewportHeight = 160;
        const int movementRows = 40;
        const int initialTop = 400;
        using var document = CreateDocument(width, height: 640);
        using var initial = document.Clone(
            new Rectangle(0, initialTop, width, viewportHeight),
            PixelFormat.Format32bppPArgb);
        var options = ScrollCaptureOptions.Default with
        {
            MinimumOverlapConfidence = 0.90,
        };
        using var composer = new ScrollCaptureComposer(
            detectStationaryLeadingRows: false);
        Assert.True(composer.TryAddFrame(initial, options, out _));

        foreach (var top in new[] { 360, 320, 280, 240 })
        {
            using var frame = document.Clone(
                new Rectangle(0, top, width, viewportHeight),
                PixelFormat.Format32bppPArgb);
            Assert.True(composer.TryAddFrame(
                frame,
                ScrollCaptureDirection.Up,
                options,
                expectedNewRows: movementRows,
                lockDirection: true,
                out _));
        }

        using var misalignedBoundary = document.Clone(
            new Rectangle(0, 240, width, viewportHeight),
            PixelFormat.Format32bppPArgb);
        using (var graphics = Graphics.FromImage(misalignedBoundary))
        using (var brush = new SolidBrush(Color.Magenta))
        {
            graphics.FillRectangle(
                brush,
                0,
                movementRows,
                width,
                viewportHeight - movementRows);
        }

        composer.RefreshBoundaryViewport(
            misalignedBoundary,
            ScrollCaptureDirection.Up,
            maximumRefreshRows: movementRows);

        using var result = composer.Compose();
        using var expected = document.Clone(
            new Rectangle(
                0,
                initialTop - (4 * movementRows),
                width,
                viewportHeight + (4 * movementRows)),
            PixelFormat.Format32bppPArgb);
        AssertPixelsEqual(expected, result, expected.Height);
    }

    [Fact]
    public void BoundaryRefreshDoesNotCopyAFixedBottomScrollbarIntoContent()
    {
        const int width = 180;
        const int viewportHeight = 160;
        const int fixedBottomRows = 20;
        using var document = CreateDocument(width, viewportHeight);
        using var frameWithScrollbar = (Bitmap)document.Clone();
        using (var graphics = Graphics.FromImage(frameWithScrollbar))
        using (var brush = new SolidBrush(Color.Gray))
        {
            graphics.FillRectangle(
                brush,
                40,
                viewportHeight - fixedBottomRows,
                100,
                fixedBottomRows);
        }

        using var composer = new ScrollCaptureComposer(
            detectStationaryLeadingRows: false);
        Assert.True(composer.TryAddFrame(
            document,
            ScrollCaptureOptions.Default,
            out _));
        composer.RefreshBoundaryViewport(
            frameWithScrollbar,
            ScrollCaptureDirection.Up,
            excludedBottomRows: fixedBottomRows);

        using var result = composer.Compose();
        AssertPixelsEqual(document, result, viewportHeight);
    }

    [Fact]
    public void RejectedUnlocatedFrameDoesNotShiftTheCapturedBoundary()
    {
        const int width = 180;
        const int viewportHeight = 160;
        using var document = CreateDocument(width, height: 240);
        using var initial = document.Clone(
            new Rectangle(0, 0, width, viewportHeight),
            PixelFormat.Format32bppPArgb);
        using var subMinimum = document.Clone(
            new Rectangle(0, 4, width, viewportHeight),
            PixelFormat.Format32bppPArgb);
        using var accepted = document.Clone(
            new Rectangle(0, 8, width, viewportHeight),
            PixelFormat.Format32bppPArgb);
        var options = ScrollCaptureOptions.Default with
        {
            MinimumNewRows = 8,
            MinimumOverlapConfidence = 0.90,
        };

        using var composer = new ControlledScrollCaptureComposer();
        composer.Initialize(initial, options);
        Assert.False(composer.TryAddDown(
            subMinimum,
            options,
            expectedRows: 4));
        Assert.Equal("no-candidate", composer.LastRejectReason);
        Assert.True(composer.TryAddDown(
            accepted,
            options,
            expectedRows: 8));

        using var result = composer.Compose();
        AssertPixelsEqual(
            document,
            result,
            viewportHeight + 8);
    }

    [Theory]
    [InlineData(119, 0)]
    [InlineData(120, 16)]
    [InlineData(362, 18)]
    [InlineData(900, 24)]
    public void FixedBottomExclusionIsBounded(
        int frameHeight,
        int expected)
    {
        Assert.Equal(
            expected,
            ControlledScrollCaptureComposer.GetFixedBottomExclusion(
                frameHeight));
    }

    private static Bitmap CreateDocument(int width, int height)
    {
        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var block = ((x / 7) + (y / 11)) % 5;
                bitmap.SetPixel(
                    x,
                    y,
                    Color.FromArgb(
                        255,
                        (y * 17 + x * 3 + block * 29) % 256,
                        (y * 7 + x * 13 + block * 41) % 256,
                        (y * 23 + x * 5 + block * 19) % 256));
            }
        }

        return bitmap;
    }

    private static Bitmap CreateSparseCodeDocument(int width, int height)
    {
        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.FromArgb(255, 30, 32, 36));
        for (var lineTop = 0; lineTop < height; lineTop += 15)
        {
            var line = lineTop / 15;
            using var lineBrush = new SolidBrush(Color.FromArgb(
                255,
                80 + (line * 17 % 150),
                90 + (line * 29 % 140),
                100 + (line * 11 % 130)));
            graphics.FillRectangle(
                lineBrush,
                18 + (line % 7) * 5,
                lineTop + 4,
                52 + (line % 5) * 13,
                2);
            graphics.FillRectangle(
                lineBrush,
                142 - (line % 4) * 9,
                lineTop + 9,
                18 + (line % 6) * 4,
                1);
        }

        return bitmap;
    }

    private static Bitmap CreatePeriodicDocument(
        int width,
        int height,
        int period)
    {
        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
        for (var y = 0; y < height; y++)
        {
            var row = y % period;
            for (var x = 0; x < width; x++)
            {
                var column = x % 31;
                bitmap.SetPixel(
                    x,
                    y,
                    Color.FromArgb(
                        255,
                        24 + ((row * 7 + column * 3) % 92),
                        30 + ((row * 11 + column * 5) % 96),
                        36 + ((row * 13 + column * 7) % 104)));
            }
        }

        return bitmap;
    }

    private static void AssertPixelsEqual(
        Bitmap expected,
        Bitmap actual,
        int height)
    {
        Assert.Equal(expected.Width, actual.Width);
        Assert.True(expected.Height >= height);
        Assert.Equal(height, actual.Height);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < actual.Width; x++)
            {
                Assert.Equal(
                    expected.GetPixel(x, y).ToArgb(),
                    actual.GetPixel(x, y).ToArgb());
            }
        }
    }
}
