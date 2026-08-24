using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Diagnostics;
using System.Reflection;
using Screenshot.App.Capture;

namespace Screenshot.App.Tests;

public sealed class ScrollCaptureComposerTests
{
    private static readonly ScrollCaptureOptions Options = new(
        MaximumFrames: 8,
        ScrollDelta: -120,
        MinimumOverlapRows: 16,
        MinimumOverlapConfidence: 0.999,
        MinimumNewRows: 8,
        FrameDelayMilliseconds: 0);

    [Fact]
    public void KeepsWheelDirectionWhenOppositeRepeatedMatchIsOnlySlightlyStronger()
    {
        var candidateType = typeof(ScrollCaptureComposer).GetNestedType(
            "AlignmentCandidate",
            BindingFlags.NonPublic);
        var selectMethod = typeof(ScrollCaptureComposer).GetMethod(
            "SelectAlignmentCandidate",
            BindingFlags.Static | BindingFlags.NonPublic);

        Assert.NotNull(candidateType);
        Assert.NotNull(selectMethod);
        var constructor = Assert.Single(candidateType.GetConstructors(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(candidate =>
                candidate.GetParameters() is { Length: 2 } parameters &&
                parameters[0].ParameterType == typeof(ScrollCaptureDirection)));
        var preferred = constructor.Invoke(
            [ScrollCaptureDirection.Up, new ImageOverlapMatch(180, 0.96)]);
        var opposite = constructor.Invoke(
            [ScrollCaptureDirection.Down, new ImageOverlapMatch(176, 0.975)]);

        var selected = selectMethod.Invoke(null, [preferred, opposite]);

        Assert.Same(preferred, selected);
    }

    [Fact]
    public void FindsTheExpectedVerticalOverlap()
    {
        using var previousFrame = CreateFrame(startY: 0, width: 96, height: 100);
        using var currentFrame = CreateFrame(startY: 30, width: 96, height: 100);

        var match = ImageOverlapMatcher.FindVerticalOverlap(
            previousFrame,
            currentFrame,
            Options.MinimumOverlapRows,
            Options.MinimumOverlapConfidence);

        Assert.NotNull(match);
        Assert.Equal(70, match.OverlapRows);
        Assert.Equal(1, match.Confidence);
    }

    [Fact]
    public void ComposesOnlyTheNewRowsFromOverlappingFrames()
    {
        using var firstFrame = CreateFrame(startY: 0, width: 96, height: 100);
        using var secondFrame = CreateFrame(startY: 30, width: 96, height: 100);
        using var composer = new ScrollCaptureComposer();

        Assert.True(composer.TryAddFrame(firstFrame, Options, out var firstMatch));
        Assert.Null(firstMatch);
        Assert.True(composer.TryAddFrame(secondFrame, Options, out var secondMatch));
        Assert.NotNull(secondMatch);
        Assert.Equal(70, secondMatch.OverlapRows);

        using var result = composer.Compose();

        Assert.Equal(96, result.Width);
        Assert.Equal(130, result.Height);

        for (var y = 0; y < result.Height; y += 7)
        {
            Assert.Equal(ExpectedColor(15, y), result.GetPixel(15, y));
        }
    }

    [Fact]
    public void RejectsFramesWithoutReliableOverlap()
    {
        using var firstFrame = CreateFrame(startY: 0, width: 96, height: 100);
        using var unrelatedFrame = CreateSolidFrame(Color.FromArgb(255, 19, 33, 57), 96, 100);
        using var composer = new ScrollCaptureComposer();

        Assert.True(composer.TryAddFrame(firstFrame, Options, out _));
        Assert.False(composer.TryAddFrame(unrelatedFrame, Options, out var overlapMatch));
        Assert.Null(overlapMatch);
        Assert.Equal(1, composer.FrameCount);
    }

    [Fact]
    public void RejectsAnUnchangedFrameAtTheEndOfScrollableContent()
    {
        using var frame = CreateFrame(startY: 0, width: 96, height: 100);
        using var composer = new ScrollCaptureComposer();

        Assert.True(composer.TryAddFrame(frame, Options, out _));
        Assert.False(composer.TryAddFrame(frame, Options, out var overlapMatch));
        Assert.Null(overlapMatch);
        Assert.Equal(1, composer.FrameCount);
    }

    [Fact]
    public void ReverseCrossingSucceedsWhenStoredTopEdgeCannotBeMatched()
    {
        // Field regression (manual wheel mode): scroll down, then scroll back
        // up past the captured start. The stored top edge frame had been
        // repainted by the live page, so the strict boundary verification
        // could never match it and every upward frame was rejected — the
        // capture stalled at "上 0" forever. A pixel-verified step from the
        // current anchor plus agreeing fresh wheel travel must cross anyway.
        const int width = 96;
        const int height = 100;
        using var initialFrame = CreateFrameWithRepaintedBand(
            startY: 40,
            width,
            height,
            staleRows: 30);
        using var firstDownFrame = CreateFrame(startY: 80, width, height);
        using var secondDownFrame = CreateFrame(startY: 120, width, height);
        using var returnKnownFrame = CreateFrame(startY: 80, width, height);
        using var approachFrame = CreateFrame(startY: 45, width, height);
        using var crossingFrame = CreateFrame(startY: 10, width, height);
        using var composer = new ScrollCaptureComposer();

        Assert.True(composer.TryAddFrame(initialFrame, Options, out _));
        Assert.True(composer.TryAddFrame(
            firstDownFrame,
            ScrollCaptureDirection.Down,
            Options,
            out _));
        Assert.True(composer.TryAddFrame(
            secondDownFrame,
            ScrollCaptureDirection.Down,
            Options,
            out _));
        Assert.Equal(180, composer.OutputHeight);

        // The wheel reverses: return through captured content. Neither step
        // adds pixels, they only move the logical anchor toward the top edge.
        Assert.False(composer.TryAddFrame(
            returnKnownFrame,
            ScrollCaptureDirection.Up,
            Options,
            expectedNewRows: 40,
            lockDirection: true,
            out _));
        Assert.False(composer.TryAddFrame(
            approachFrame,
            ScrollCaptureDirection.Up,
            Options,
            expectedNewRows: 35,
            lockDirection: true,
            out _));
        Assert.Equal(0, composer.AddedAboveFrameCount);

        Assert.True(composer.TryAddFrame(
            crossingFrame,
            ScrollCaptureDirection.Up,
            Options,
            expectedNewRows: 35,
            lockDirection: true,
            out _));
        Assert.Equal(1, composer.AddedAboveFrameCount);
        Assert.Equal(210, composer.OutputHeight);

        using var result = composer.Compose();
        Assert.Equal(210, result.Height);
        for (var y = 0; y < 30; y += 5)
        {
            Assert.Equal(
                ExpectedColor(15, 10 + y),
                result.GetPixel(15, y));
        }
    }

    [Fact]
    public void RejectsRepeatedViewportFramesAtBothScrollableBoundaries()
    {
        const int width = 120;
        const int height = 100;
        using var initialFrame = CreateRepeatingFrame(
            width,
            height,
            scrollbarColor: Color.Red);
        using var bottomBoundaryFrame = CreateRepeatingFrame(
            width,
            height,
            scrollbarColor: Color.Blue);
        using var topBoundaryFrame = CreateRepeatingFrame(
            width,
            height,
            scrollbarColor: Color.Green);
        using var bottomComposer = new ScrollCaptureComposer();
        using var topComposer = new ScrollCaptureComposer();

        Assert.True(bottomComposer.TryAddFrame(initialFrame, Options, out _));
        Assert.False(bottomComposer.TryAddFrame(
            bottomBoundaryFrame,
            ScrollCaptureDirection.Down,
            Options,
            out var bottomMatch));
        Assert.Null(bottomMatch);
        Assert.Equal(1, bottomComposer.FrameCount);

        Assert.True(topComposer.TryAddFrame(initialFrame, Options, out _));
        Assert.False(topComposer.TryAddFrame(
            topBoundaryFrame,
            ScrollCaptureDirection.Up,
            Options,
            out var topMatch));
        Assert.Null(topMatch);
        Assert.Equal(1, topComposer.FrameCount);
    }

    [Fact]
    public void RejectsASlightlyAnimatedViewportAtTheScrollableBoundary()
    {
        const int width = 120;
        const int height = 100;
        var permissiveOptions = Options with
        {
            MinimumOverlapConfidence = 0.90,
        };
        using var initialFrame = CreateRepeatingFrame(
            width,
            height,
            scrollbarColor: Color.Red);
        using var animatedBoundaryFrame = CreateRepeatingFrame(
            width,
            height,
            scrollbarColor: Color.Blue);
        using var graphics = Graphics.FromImage(animatedBoundaryFrame);
        using var animationBrush = new SolidBrush(Color.Magenta);
        graphics.FillRectangle(animationBrush, 20, 45, 10, 10);
        using var composer = new ScrollCaptureComposer();

        Assert.True(composer.TryAddFrame(initialFrame, permissiveOptions, out _));
        Assert.False(composer.TryAddFrame(
            animatedBoundaryFrame,
            ScrollCaptureDirection.Down,
            permissiveOptions,
            out var overlapMatch));
        Assert.Null(overlapMatch);
        Assert.Equal(1, composer.FrameCount);
        Assert.Equal(height, composer.OutputHeight);
    }

    [Fact]
    public void RejectsAChangedCurrentLineAtTheScrollableBoundary()
    {
        const int width = 320;
        const int height = 288;
        using var initialFrame = CreateCodeEditorContent(width, height);
        using var highlightedFrame = (Bitmap)initialFrame.Clone();
        using (var graphics = Graphics.FromImage(highlightedFrame))
        using (var highlightBrush = new SolidBrush(Color.FromArgb(255, 42, 45, 51)))
        {
            graphics.FillRectangle(highlightBrush, 48, 126, width - 60, 18);
        }

        using var composer = new ScrollCaptureComposer();
        Assert.True(composer.TryAddFrame(initialFrame, Options, out _));
        Assert.False(composer.TryAddFrame(
            highlightedFrame,
            ScrollCaptureDirection.Up,
            Options,
            expectedNewRows: 48,
            lockDirection: true,
            out var overlapMatch));

        Assert.Null(overlapMatch);
        Assert.Equal(1, composer.FrameCount);
        Assert.Equal(height, composer.OutputHeight);
        Assert.Equal(0, composer.AddedAboveFrameCount);
        Assert.Equal(0, composer.AddedBelowFrameCount);
    }

    [Fact]
    public void RejectsSubMinimumSmoothScrollWithAChangedCurrentLine()
    {
        const int width = 320;
        const int height = 288;
        using var initialFrame = CreateCodeEditorContent(width, height + 2);
        using var firstViewport = Crop(initialFrame, 0, height);
        using var microMovedViewport = Crop(initialFrame, 2, height);
        using (var graphics = Graphics.FromImage(microMovedViewport))
        using (var highlightBrush = new SolidBrush(Color.FromArgb(255, 42, 45, 51)))
        {
            graphics.FillRectangle(highlightBrush, 48, 124, width - 60, 18);
        }

        using var composer = new ScrollCaptureComposer();
        Assert.True(composer.TryAddFrame(firstViewport, Options, out _));
        Assert.False(composer.TryAddFrame(
            microMovedViewport,
            ScrollCaptureDirection.Down,
            Options,
            expectedNewRows: 48,
            lockDirection: true,
            out var overlapMatch));

        Assert.Null(overlapMatch);
        Assert.Equal(height, composer.OutputHeight);
    }

    [Fact]
    public void FindsOverlapWithAStickyHeaderAndChangingScrollbar()
    {
        const int width = 140;
        const int height = 180;
        const int headerRows = 36;
        const int scrollDistance = 48;
        using var previousFrame = CreateBrowserLikeFrame(
            startY: 0,
            width,
            height,
            headerRows,
            scrollbarColor: Color.Red);
        using var currentFrame = CreateBrowserLikeFrame(
            startY: scrollDistance,
            width,
            height,
            headerRows,
            scrollbarColor: Color.Blue);

        var match = ImageOverlapMatcher.FindVerticalOverlap(
            previousFrame,
            currentFrame,
            minimumOverlapRows: 24,
            minimumConfidence: 0.99,
            minimumNewRows: 8);

        Assert.NotNull(match);
        Assert.Equal(height - scrollDistance, match.OverlapRows);
    }

    [Fact]
    public void MatchesALargeBrowserFrameWithinAnInteractiveBudget()
    {
        const int width = 640;
        const int height = 480;
        const int headerRows = 72;
        const int scrollDistance = 96;
        using var previousFrame = CreateBrowserLikeFrame(
            startY: 0,
            width,
            height,
            headerRows,
            scrollbarColor: Color.DarkGray);
        using var currentFrame = CreateBrowserLikeFrame(
            startY: scrollDistance,
            width,
            height,
            headerRows,
            scrollbarColor: Color.LightGray);
        var stopwatch = Stopwatch.StartNew();

        var match = ImageOverlapMatcher.FindVerticalOverlap(
            previousFrame,
            currentFrame,
            minimumOverlapRows: 24,
            minimumConfidence: 0.99,
            minimumNewRows: 8);

        stopwatch.Stop();
        Assert.NotNull(match);
        Assert.Equal(height - scrollDistance, match.OverlapRows);
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(1),
            $"匹配耗时 {stopwatch.Elapsed.TotalMilliseconds:F0}ms。");
    }

    [Fact]
    public void KeepsSparseChatFramesContiguousWhenOneMessageChanges()
    {
        const int width = 240;
        const int height = 260;
        const int scrollDistance = 48;
        var chatOptions = Options with
        {
            MinimumOverlapRows = 24,
            MinimumOverlapConfidence = 0.96,
        };
        using var content = CreateSparseChatContent(
            width,
            height + (scrollDistance * 2));
        using var firstFrame = Crop(content, 0, height);
        using var secondFrame = Crop(content, scrollDistance, height);
        using var thirdFrame = Crop(content, scrollDistance * 2, height);
        using var changingGraphics = Graphics.FromImage(secondFrame);
        using var changingBrush = new SolidBrush(Color.FromArgb(255, 206, 104, 80));
        changingGraphics.FillRectangle(changingBrush, 20, 70, 56, 20);
        using var composer = new ScrollCaptureComposer();

        Assert.True(composer.TryAddFrame(firstFrame, chatOptions, out _));
        Assert.True(composer.TryAddFrame(
            secondFrame,
            ScrollCaptureDirection.Down,
            chatOptions,
            out var secondMatch));
        Assert.NotNull(secondMatch);
        Assert.Equal(height - scrollDistance, secondMatch.OverlapRows);
        Assert.True(composer.TryAddFrame(
            thirdFrame,
            ScrollCaptureDirection.Down,
            chatOptions,
            out var thirdMatch));
        Assert.NotNull(thirdMatch);
        Assert.Equal(height - scrollDistance, thirdMatch.OverlapRows);

        using var result = composer.Compose();

        Assert.Equal(height + (scrollDistance * 2), result.Height);

        for (var y = 0; y < result.Height; y += 5)
        {
            for (var x = 0; x < result.Width; x += 11)
            {
                Assert.Equal(content.GetPixel(x, y), result.GetPixel(x, y));
            }
        }
    }

    [Fact]
    public void UsesLineNumbersToAlignRepeatedCodeRows()
    {
        const int width = 320;
        const int height = 288;
        const int scrollDistance = 36;
        var codeOptions = Options with
        {
            MaximumFrames = 12,
            MinimumOverlapRows = 24,
            MinimumOverlapConfidence = 0.96,
        };
        using var content = CreateCodeEditorContent(
            width,
            height + (scrollDistance * 2));
        using var firstFrame = Crop(content, 0, height);
        using var secondFrame = Crop(content, scrollDistance, height);
        using var thirdFrame = Crop(content, scrollDistance * 2, height);
        using var composer = new ScrollCaptureComposer();

        Assert.True(composer.TryAddFrame(firstFrame, codeOptions, out _));
        Assert.True(composer.TryAddFrame(
            secondFrame,
            ScrollCaptureDirection.Down,
            codeOptions,
            out var secondMatch));
        Assert.NotNull(secondMatch);
        Assert.Equal(height - scrollDistance, secondMatch.OverlapRows);
        Assert.True(composer.TryAddFrame(
            thirdFrame,
            ScrollCaptureDirection.Down,
            codeOptions,
            out var thirdMatch));
        Assert.NotNull(thirdMatch);
        Assert.Equal(height - scrollDistance, thirdMatch.OverlapRows);

        using var result = composer.Compose();

        Assert.Equal(height + (scrollDistance * 2), result.Height);

        for (var y = 0; y < result.Height; y += 3)
        {
            for (var x = 0; x < result.Width; x += 7)
            {
                Assert.Equal(content.GetPixel(x, y), result.GetPixel(x, y));
            }
        }
    }

    [Fact]
    public void PreviewScalesTheCompletedCompositeOnce()
    {
        var previewOptions = Options with
        {
            MaximumFrames = 40,
            MinimumOverlapRows = 16,
            MinimumOverlapConfidence = 0.999,
        };
        using var composer = new ScrollCaptureComposer();

        for (var startY = 0; startY <= 160; startY += 8)
        {
            using var frame = CreateFrame(startY, width: 96, height: 100);
            Assert.True(composer.TryAddFrame(frame, previewOptions, out _));
        }

        using var composite = composer.Compose();
        using var preview = composer.ComposePreview(48, 80);
        using var expected = new Bitmap(
            preview.Width,
            preview.Height,
            PixelFormat.Format32bppPArgb);
        using (var graphics = Graphics.FromImage(expected))
        {
            graphics.Clear(Color.FromArgb(255, 246, 248, 249));
            graphics.InterpolationMode = InterpolationMode.HighQualityBilinear;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.DrawImage(
                composite,
                new Rectangle(0, 0, preview.Width, preview.Height),
                new Rectangle(0, 0, composite.Width, composite.Height),
                GraphicsUnit.Pixel);
        }

        for (var y = 0; y < preview.Height; y++)
        {
            for (var x = 0; x < preview.Width; x++)
            {
                Assert.Equal(expected.GetPixel(x, y), preview.GetPixel(x, y));
            }
        }
    }

    [Fact]
    public void LivePreviewShowsTheWholeCapturedImage()
    {
        var previewOptions = Options with
        {
            MaximumFrames = 20,
            MinimumOverlapRows = 16,
            MinimumOverlapConfidence = 0.999,
        };
        using var composer = new ScrollCaptureComposer();

        foreach (var top in new[] { 0, 40, 80, 120, 160 })
        {
            using var frame = CreateFrame(top, width: 96, height: 100);
            Assert.True(composer.TryAddFrame(frame, previewOptions, out _));
        }

        // Within the height budget the preview is the whole image, unscaled.
        using (var preview = composer.ComposeLivePreview(96, 300))
        {
            Assert.Equal(96, preview.Width);
            Assert.Equal(260, preview.Height);
            for (var y = 0; y < preview.Height; y += 7)
            {
                Assert.Equal(ExpectedColor(15, y), preview.GetPixel(15, y));
            }
        }

        // Beyond the budget the whole image is scaled down, aspect preserved,
        // never cropped to a slice.
        using (var preview = composer.ComposeLivePreview(96, 80))
        {
            Assert.Equal(80, preview.Height);
            Assert.Equal(
                (int)Math.Round(96 * (80 / 260d)),
                preview.Width);
        }
    }

    [Fact]
    public void PrependsUpwardContentAndAppendsDownwardContent()
    {
        using var initialFrame = CreateFrame(startY: 100, width: 96, height: 100);
        using var downwardFrame = CreateFrame(startY: 130, width: 96, height: 100);
        using var upwardFrame = CreateFrame(startY: 70, width: 96, height: 100);
        using var composer = new ScrollCaptureComposer();

        Assert.True(composer.TryAddFrame(initialFrame, Options, out _));
        Assert.True(composer.TryAddFrame(
            downwardFrame,
            ScrollCaptureDirection.Down,
            Options,
            out _));
        Assert.True(composer.TryAddFrame(
            upwardFrame,
            ScrollCaptureDirection.Up,
            Options,
            out _));

        using var result = composer.Compose();
        using var preview = composer.ComposePreview(
            maximumWidth: 48,
            maximumHeight: 80);

        Assert.Equal(3, composer.FrameCount);
        Assert.Equal(1, composer.AddedAboveFrameCount);
        Assert.Equal(1, composer.AddedBelowFrameCount);
        Assert.Equal(160, composer.OutputHeight);
        Assert.Equal(48, preview.Width);
        Assert.Equal(80, preview.Height);

        for (var y = 0; y < result.Height; y += 7)
        {
            Assert.Equal(ExpectedColor(15, y + 70), result.GetPixel(15, y));
        }
    }

    [Fact]
    public void DoesNotDuplicateContentWhenScrollingBackThroughCapturedFrames()
    {
        using var initialFrame = CreateFrame(startY: 100, width: 96, height: 100);
        using var downwardFrame = CreateFrame(startY: 130, width: 96, height: 100);
        using var returnedInitialFrame = CreateFrame(startY: 100, width: 96, height: 100);
        using var upwardFrame = CreateFrame(startY: 70, width: 96, height: 100);
        using var returnedDownwardFrame = CreateFrame(startY: 130, width: 96, height: 100);
        using var fartherDownwardFrame = CreateFrame(startY: 160, width: 96, height: 100);
        using var composer = new ScrollCaptureComposer();

        Assert.True(composer.TryAddFrame(initialFrame, Options, out _));
        Assert.True(composer.TryAddFrame(
            downwardFrame,
            ScrollCaptureDirection.Down,
            Options,
            out _));

        Assert.False(composer.TryAddFrame(
            returnedInitialFrame,
            ScrollCaptureDirection.Up,
            Options,
            out _));

        Assert.True(composer.TryAddFrame(
            upwardFrame,
            ScrollCaptureDirection.Up,
            Options,
            out _));

        Assert.False(composer.TryAddFrame(
            returnedInitialFrame,
            ScrollCaptureDirection.Down,
            Options,
            out _));
        Assert.False(composer.TryAddFrame(
            returnedDownwardFrame,
            ScrollCaptureDirection.Down,
            Options,
            out _));

        Assert.True(composer.TryAddFrame(
            fartherDownwardFrame,
            ScrollCaptureDirection.Down,
            Options,
            out _));

        Assert.Equal(4, composer.FrameCount);
        Assert.Equal(1, composer.AddedAboveFrameCount);
        Assert.Equal(2, composer.AddedBelowFrameCount);
        Assert.Equal(190, composer.OutputHeight);

        using var result = composer.Compose();

        for (var y = 0; y < result.Height; y += 7)
        {
            Assert.Equal(ExpectedColor(15, y + 70), result.GetPixel(15, y));
        }
    }

    [Fact]
    public void ConfirmedBottomCannotAppendContentAfterReturningToTheBoundary()
    {
        const int width = 96;
        const int height = 120;
        using var composer = new ScrollCaptureComposer();

        foreach (var top in new[] { 0, 80, 160 })
        {
            using var frame = CreateFrame(top, width, height);
            Assert.True(composer.TryAddFrame(
                frame,
                ScrollCaptureDirection.Down,
                Options,
                out _));
        }

        var confirmedHeight = composer.OutputHeight;
        using (var unrelatedMarker = CreateFrame(240, width, height))
        {
            Assert.False(composer.TryMarkBoundaryReached(
                unrelatedMarker,
                ScrollCaptureDirection.Down));
        }

        using (var actualBoundaryMarker = CreateFrame(160, width, height))
        {
            Assert.True(composer.TryMarkBoundaryReached(
                actualBoundaryMarker,
                ScrollCaptureDirection.Down));
        }

        using (var returnFrame = CreateFrame(80, width, height))
        {
            Assert.False(composer.TryAddFrame(
                returnFrame,
                ScrollCaptureDirection.Up,
                Options,
                out _));
            Assert.NotNull(composer.LastFrameMovementRows);
        }

        using (var boundaryFrame = CreateFrame(160, width, height))
        {
            Assert.False(composer.TryAddFrame(
                boundaryFrame,
                ScrollCaptureDirection.Down,
                Options,
                out _));
        }

        // Even a perfectly matching frame that appears to continue past the
        // edge cannot override the physical boundary evidence.
        using (var beyondBoundaryFrame = CreateFrame(240, width, height))
        {
            Assert.False(composer.TryAddFrame(
                beyondBoundaryFrame,
                ScrollCaptureDirection.Down,
                Options,
                out _));
            Assert.Equal("confirmed-bottom", composer.LastRejectReason);
        }

        Assert.Equal(confirmedHeight, composer.OutputHeight);
    }

    [Fact]
    public void PrependsNewContentAtTopAfterScrollingDownThenBackUp()
    {
        // Regression: after down-capture and return to the top boundary, further
        // Up with unseen content must prepend. Known-viewport matching must not
        // lock onto the initial frame (FrameTop equality) and block expansion.
        using var initialFrame = CreateFrame(startY: 100, width: 96, height: 100);
        using var downwardFrame = CreateFrame(startY: 140, width: 96, height: 100);
        using var returnedInitialFrame = CreateFrame(startY: 100, width: 96, height: 100);
        using var furtherUpFrame = CreateFrame(startY: 50, width: 96, height: 100);
        using var composer = new ScrollCaptureComposer();

        Assert.True(composer.TryAddFrame(initialFrame, Options, out _));
        Assert.True(composer.TryAddFrame(
            downwardFrame,
            ScrollCaptureDirection.Down,
            Options,
            out _));
        Assert.False(composer.TryAddFrame(
            returnedInitialFrame,
            ScrollCaptureDirection.Up,
            Options,
            out _));

        // At the top boundary: additional Up must expand, not ReplaceCurrentFrame.
        Assert.True(composer.TryAddFrame(
            furtherUpFrame,
            ScrollCaptureDirection.Up,
            Options,
            out _));

        Assert.Equal(3, composer.FrameCount);
        Assert.Equal(1, composer.AddedAboveFrameCount);
        Assert.Equal(1, composer.AddedBelowFrameCount);
        Assert.Equal(190, composer.OutputHeight);

        using var result = composer.Compose();
        for (var y = 0; y < result.Height; y += 5)
        {
            Assert.Equal(ExpectedColor(15, y + 50), result.GetPixel(15, y));
        }
    }

    [Fact]
    public void PrependsAboveStickyChatAfterDownThenUpBoundary()
    {
        // Chat UIs share sticky chrome; fingerprints near the top can look similar
        // to the initial viewport. After down then back to top, further Up must
        // still prepend new message rows rather than treat the frame as known.
        const int width = 280;
        const int height = 320;
        const int startTop = 80;
        const int downStep = 48;
        const int furtherUpTop = 20;
        var options = Options with
        {
            MaximumFrames = 20,
            MinimumOverlapRows = 24,
            MinimumOverlapConfidence = 0.96,
        };
        using var content = CreateWeChatLikeChatContent(width, height + startTop + downStep);
        using var composer = new ScrollCaptureComposer();

        using (var initial = Crop(content, startTop, height))
        {
            Assert.True(composer.TryAddFrame(initial, options, out _));
        }

        using (var down = Crop(content, startTop + downStep, height))
        {
            Assert.True(composer.TryAddFrame(
                down,
                ScrollCaptureDirection.Down,
                options,
                out _));
        }

        using (var returned = Crop(content, startTop, height))
        {
            Assert.False(composer.TryAddFrame(
                returned,
                ScrollCaptureDirection.Up,
                options,
                out _));
        }

        using (var furtherUp = Crop(content, furtherUpTop, height))
        {
            Assert.True(
                composer.TryAddFrame(
                    furtherUp,
                    ScrollCaptureDirection.Up,
                    options,
                    out _),
                "Further Up at the top boundary must prepend unseen chat content.");
        }

        Assert.True(composer.AddedAboveFrameCount >= 1);
        Assert.Equal(height + startTop + downStep - furtherUpTop, composer.OutputHeight);

        using var result = composer.Compose();
        for (var y = 0; y < result.Height; y += 4)
        {
            for (var x = 0; x < result.Width; x += 11)
            {
                Assert.Equal(content.GetPixel(x, y + furtherUpTop), result.GetPixel(x, y));
            }
        }
    }
    [Fact]
    public void PrependsUnseenContentWhenTheWheelDirectionIsStale()
    {
        using var initialFrame = CreateFrame(startY: 100, width: 96, height: 100);
        using var downwardFrame = CreateFrame(startY: 150, width: 96, height: 100);
        using var returnedInitialFrame = CreateFrame(
            startY: 100,
            width: 96,
            height: 100);
        using var upwardFrame = CreateFrame(startY: 50, width: 96, height: 100);
        using var composer = new ScrollCaptureComposer();

        Assert.True(composer.TryAddFrame(initialFrame, Options, out _));
        Assert.True(composer.TryAddFrame(
            downwardFrame,
            ScrollCaptureDirection.Down,
            Options,
            out _));

        // The wheel monitor can report the prior direction while a smooth
        // viewport has already started moving back. The image alignment must
        // recognize the old viewport and then use the reverse displacement.
        Assert.False(composer.TryAddFrame(
            returnedInitialFrame,
            ScrollCaptureDirection.Down,
            Options,
            out _));
        Assert.True(composer.TryAddFrame(
            upwardFrame,
            ScrollCaptureDirection.Down,
            Options,
            out _));

        Assert.Equal(3, composer.FrameCount);
        Assert.Equal(1, composer.AddedAboveFrameCount);
        Assert.Equal(1, composer.AddedBelowFrameCount);

        using var result = composer.Compose();

        Assert.Equal(200, result.Height);

        for (var y = 0; y < result.Height; y += 7)
        {
            Assert.Equal(ExpectedColor(15, y + 50), result.GetPixel(15, y));
        }
    }

    [Fact]
    public void RecognizesAReturnedViewportDespiteASmallVisualUpdate()
    {
        using var initialFrame = CreateFrame(startY: 100, width: 120, height: 120);
        using var downwardFrame = CreateFrame(startY: 160, width: 120, height: 120);
        using var returnedInitialFrame = CreateFrame(
            startY: 100,
            width: 120,
            height: 120);
        using var updateGraphics = Graphics.FromImage(returnedInitialFrame);
        using var updateBrush = new SolidBrush(Color.Magenta);
        updateGraphics.FillRectangle(updateBrush, 30, 48, 12, 12);
        using var upperFrame = CreateFrame(startY: 40, width: 120, height: 120);
        using var composer = new ScrollCaptureComposer();
        var dynamicOptions = Options with
        {
            MinimumOverlapRows = 24,
            MinimumOverlapConfidence = 0.93,
        };

        Assert.True(composer.TryAddFrame(initialFrame, dynamicOptions, out _));
        Assert.True(composer.TryAddFrame(
            downwardFrame,
            ScrollCaptureDirection.Down,
            dynamicOptions,
            out _));
        Assert.False(composer.TryAddFrame(
            returnedInitialFrame,
            ScrollCaptureDirection.Up,
            dynamicOptions,
            out _));
        Assert.True(composer.TryAddFrame(
            upperFrame,
            ScrollCaptureDirection.Up,
            dynamicOptions,
            out _));

        Assert.Equal(3, composer.FrameCount);
        Assert.Equal(1, composer.AddedAboveFrameCount);
        Assert.Equal(1, composer.AddedBelowFrameCount);
    }

    [Fact]
    public void DoesNotPrependDynamicStickyDashboardFramesDuringReverseScroll()
    {
        const int width = 360;
        const int height = 260;
        const int stickyRows = 52;
        var options = Options with
        {
            MaximumFrames = 30,
            MinimumOverlapRows = 24,
            MinimumOverlapConfidence = 0.96,
        };
        using var composer = new ScrollCaptureComposer();

        foreach (var top in new[] { 0, 72, 144, 216, 288 })
        {
            using var frame = CreateStickyDashboardFrame(
                top,
                width,
                height,
                stickyRows,
                updateSeed: 0);
            composer.TryAddFrame(
                frame,
                ScrollCaptureDirection.Down,
                options,
                expectedNewRows: 72,
                lockDirection: true,
                out _);
        }

        var capturedHeight = composer.OutputHeight;
        var addedAbove = composer.AddedAboveFrameCount;
        var addedBelow = composer.AddedBelowFrameCount;

        // Return through already captured content. Each frame changes a wide
        // dashboard value band and the scrollbar, matching the real failure
        // where strict pixel fingerprints missed the old viewport and the
        // composer prepended duplicate rows in the middle of the result.
        var updateSeed = 1;
        foreach (var top in new[] { 216, 144, 72, 0, 72, 144, 72, 0 })
        {
            var direction = top <= 72
                ? ScrollCaptureDirection.Up
                : updateSeed % 3 == 0
                    ? ScrollCaptureDirection.Down
                    : ScrollCaptureDirection.Up;
            using var frame = CreateStickyDashboardFrame(
                top,
                width,
                height,
                stickyRows,
                updateSeed++);
            composer.TryAddFrame(
                frame,
                direction,
                options,
                expectedNewRows: 72,
                lockDirection: true,
                out _);
        }

        Assert.Equal(capturedHeight, composer.OutputHeight);
        Assert.Equal(addedAbove, composer.AddedAboveFrameCount);
        Assert.Equal(addedBelow, composer.AddedBelowFrameCount);
    }

    [Fact]
    public void UpwardExpansionDoesNotRepeatAStickyDashboardHeader()
    {
        const int width = 360;
        const int height = 260;
        const int stickyRows = 52;
        var options = Options with
        {
            MaximumFrames = 30,
            MinimumOverlapRows = 24,
            MinimumOverlapConfidence = 0.96,
        };
        using var composer = new ScrollCaptureComposer();

        foreach (var (top, direction) in new[]
                 {
                     (300, ScrollCaptureDirection.Down),
                     (372, ScrollCaptureDirection.Down),
                     (444, ScrollCaptureDirection.Down),
                     (372, ScrollCaptureDirection.Up),
                     (300, ScrollCaptureDirection.Up),
                     (228, ScrollCaptureDirection.Up),
                     (156, ScrollCaptureDirection.Up),
                 })
        {
            using var frame = CreateStickyDashboardFrame(
                top,
                width,
                height,
                stickyRows,
                updateSeed: 0);
            composer.TryAddFrame(
                frame,
                direction,
                options,
                expectedNewRows: 72,
                lockDirection: true,
                out _);
        }

        Assert.Equal(height + 288, composer.OutputHeight);
        using var result = composer.Compose();
        var headerControlColor = Color.FromArgb(255, 29, 49, 76).ToArgb();
        var matchingRows = Enumerable.Range(0, result.Height)
            .Count(y => result.GetPixel(20, y).ToArgb() == headerControlColor);

        Assert.InRange(matchingRows, 20, 32);
    }

    [Fact]
    public void ReturnFingerprintToleratesDynamicValuesButRejectsAnotherViewport()
    {
        const int width = 360;
        const int height = 260;
        const int stickyRows = 52;
        using var original = CreateStickyDashboardFrame(
            144,
            width,
            height,
            stickyRows,
            updateSeed: 0);
        using var updated = CreateStickyDashboardFrame(
            144,
            width,
            height,
            stickyRows,
            updateSeed: 7);
        using var anotherViewport = CreateStickyDashboardFrame(
            216,
            width,
            height,
            stickyRows,
            updateSeed: 7);
        var originalFingerprint = ViewportFingerprint.Create(original);
        var updatedFingerprint = ViewportFingerprint.Create(updated);
        var anotherFingerprint = ViewportFingerprint.Create(anotherViewport);

        Assert.False(originalFingerprint.IsSimilarTo(updatedFingerprint));
        Assert.True(originalFingerprint.IsPreviouslySeenComparedTo(updatedFingerprint));
        Assert.False(originalFingerprint.IsPreviouslySeenComparedTo(anotherFingerprint));
    }

    [Fact]
    public void ReturnFingerprintLocatesShiftedDynamicDashboardViewport()
    {
        const int width = 360;
        const int height = 260;
        const int stickyRows = 52;
        using var original = CreateStickyDashboardFrame(
            144,
            width,
            height,
            stickyRows,
            updateSeed: 0);
        using var shifted = CreateStickyDashboardFrame(
            177,
            width,
            height,
            stickyRows,
            updateSeed: 9);
        var originalFingerprint = ViewportFingerprint.Create(original);
        var shiftedFingerprint = ViewportFingerprint.Create(shifted);

        var found = originalFingerprint.TryLocatePreviouslySeenComparedTo(
            shiftedFingerprint,
            maximumPixelShift: 64,
            out var shift,
            out var score);
        Assert.True(found, $"shift={shift}, score={score}");
        Assert.InRange(shift, 29, 37);

        using var unrelated = CreateRepeatingFrame(
            width,
            height,
            Color.Magenta);
        var unrelatedFingerprint = ViewportFingerprint.Create(unrelated);
        Assert.False(originalFingerprint.TryLocatePreviouslySeenComparedTo(
            unrelatedFingerprint,
            maximumPixelShift: 64,
            out _,
            out _));
    }

    [Fact]
    public void ShiftedDynamicReturnDoesNotDuplicateCapturedDashboardRows()
    {
        const int width = 360;
        const int height = 260;
        const int stickyRows = 52;
        var options = Options with
        {
            MaximumFrames = 40,
            MinimumOverlapRows = 24,
            MinimumOverlapConfidence = 0.96,
        };
        using var composer = new ScrollCaptureComposer();

        foreach (var top in new[] { 0, 80, 160, 240, 320 })
        {
            using var frame = CreateStickyDashboardFrame(
                top,
                width,
                height,
                stickyRows,
                updateSeed: 0);
            composer.TryAddFrame(
                frame,
                ScrollCaptureDirection.Down,
                options,
                expectedNewRows: 80,
                lockDirection: true,
                out _);
        }

        var capturedHeight = composer.OutputHeight;
        var addedAbove = composer.AddedAboveFrameCount;
        var addedBelow = composer.AddedBelowFrameCount;

        var seed = 1;
        foreach (var top in new[] { 287, 203, 119, 35 })
        {
            using var frame = CreateStickyDashboardFrame(
                top,
                width,
                height,
                stickyRows,
                updateSeed: seed++);
            composer.TryAddFrame(
                frame,
                ScrollCaptureDirection.Up,
                options,
                expectedNewRows: 84,
                lockDirection: true,
                out _);
        }

        Assert.Equal(capturedHeight, composer.OutputHeight);
        Assert.Equal(addedAbove, composer.AddedAboveFrameCount);
        Assert.Equal(addedBelow, composer.AddedBelowFrameCount);
    }

    [Fact]
    public void UsesWheelTravelToResolveAPeriodicTopBoundaryCrossing()
    {
        const int width = 320;
        const int height = 240;
        const int initialTop = 300;
        const int downTop = 318;
        const int upperTop = 118;
        var options = Options with
        {
            MinimumOverlapRows = 24,
            MinimumOverlapConfidence = 0.96,
        };
        using var content = CreatePeriodicBandContent(
            width,
            initialTop + height + 32,
            period: 44);
        using var initial = Crop(content, initialTop, height);
        using var down = Crop(content, downTop, height);
        using var upper = Crop(content, upperTop, height);
        using var composer = new ScrollCaptureComposer();

        Assert.True(composer.TryAddFrame(initial, options, out _));
        Assert.True(composer.TryAddFrame(
            down,
            ScrollCaptureDirection.Down,
            options,
            expectedNewRows: downTop - initialTop,
            lockDirection: true,
            out _));
        Assert.True(composer.TryAddFrame(
            upper,
            ScrollCaptureDirection.Up,
            options,
            expectedNewRows: downTop - upperTop,
            lockDirection: true,
            out _));

        Assert.Equal(downTop + height - upperTop, composer.OutputHeight);
        Assert.Equal(1, composer.AddedAboveFrameCount);
    }

    [Fact]
    public void WheelReturnWalkDoesNotSearchOrAppendInsideCapturedRange()
    {
        const int width = 160;
        const int height = 140;
        using var composer = new ScrollCaptureComposer();

        foreach (var top in new[] { 0, 50, 100 })
        {
            using var frame = CreateFrame(top, width, height);
            composer.TryAddFrame(
                frame,
                ScrollCaptureDirection.Down,
                Options,
                expectedNewRows: top == 0 ? null : 50,
                lockDirection: true,
                out _);
        }

        var capturedHeight = composer.OutputHeight;
        using var unrelatedLiveFrame = CreateRepeatingFrame(
            width,
            height,
            Color.Magenta);

        Assert.False(composer.TryAddFrame(
            unrelatedLiveFrame,
            ScrollCaptureDirection.Up,
            Options,
            expectedNewRows: 40,
            lockDirection: true,
            out _));
        Assert.Equal("wheel-return-reanchor", composer.LastRejectReason);
        Assert.Equal(40, composer.LastFrameMovementRows);
        Assert.Equal(capturedHeight, composer.OutputHeight);
    }

    [Fact]
    public void ReturnWalkCrossesTopUsingTheStoredBoundaryAfterLosingCurrentAnchor()
    {
        const int width = 320;
        const int height = 240;
        const int initialTop = 300;
        const int bottomTop = 500;
        const int upperTop = 250;
        var options = Options with
        {
            MinimumOverlapRows = 24,
            MinimumOverlapConfidence = 0.96,
        };
        using var document = CreateCodeEditorContent(width, bottomTop + height + 20);
        using var initial = Crop(document, initialTop, height);
        using var bottom = Crop(document, bottomTop, height);
        using var upper = Crop(document, upperTop, height);
        using var unrelatedReturnFrame = CreateRepeatingFrame(
            width,
            height,
            Color.Magenta);
        using var composer = new ScrollCaptureComposer();

        Assert.True(composer.TryAddFrame(initial, options, out _));
        Assert.True(composer.TryAddFrame(
            bottom,
            ScrollCaptureDirection.Down,
            options,
            expectedNewRows: bottomTop - initialTop,
            lockDirection: true,
            out _));

        // The first reverse sample has no usable image relation, as happens
        // when matching falls behind a fling. It can safely walk inside the
        // already captured range using wheel evidence.
        Assert.False(composer.TryAddFrame(
            unrelatedReturnFrame,
            ScrollCaptureDirection.Up,
            options,
            expectedNewRows: 100,
            lockDirection: true,
            out _));
        Assert.Equal("wheel-return-reanchor", composer.LastRejectReason);

        // The next sample crosses the original top. It cannot match the
        // unrelated current anchor, but it does overlap the stored top frame
        // and must prepend immediately on this same upward pass.
        Assert.True(composer.TryAddFrame(
            upper,
            ScrollCaptureDirection.Up,
            options,
            expectedNewRows: 150,
            lockDirection: true,
            out _));

        Assert.Equal(bottomTop + height - upperTop, composer.OutputHeight);
        Assert.Equal(1, composer.AddedAboveFrameCount);
    }

    [Fact]
    public void ReturningToCapturedTopDoesNotPrependSettlingFramesAfterAGap()
    {
        const int width = 320;
        const int height = 240;
        const int downTop = 180;
        var options = Options with
        {
            MinimumOverlapRows = 24,
            MinimumOverlapConfidence = 0.96,
        };
        using var document = CreateCodeEditorContent(width, downTop + height + 20);
        using var initial = Crop(document, 0, height);
        using var down = Crop(document, downTop, height);
        using var unrelatedTransition = CreateRepeatingFrame(
            width,
            height,
            Color.Magenta);
        using var composer = new ScrollCaptureComposer();

        Assert.True(composer.TryAddFrame(initial, options, out _));
        Assert.True(composer.TryAddFrame(
            down,
            ScrollCaptureDirection.Down,
            options,
            expectedNewRows: downTop,
            lockDirection: true,
            out _));

        // Walk back to the exact starting viewport. This establishes a return
        // leg through content that is already part of the result.
        Assert.False(composer.TryAddFrame(
            initial,
            ScrollCaptureDirection.Up,
            options,
            expectedNewRows: downTop,
            lockDirection: true,
            out _));
        Assert.Equal(0, composer.CurrentFrameTop);

        // A paint transition at the physical top cannot be located. The next
        // few frames shift the original viewport down by small amounts, just
        // like a sticky header settling. They have perfect overlap and used to
        // be prepended as new rows, duplicating the page title.
        Assert.False(composer.TryAddFrame(
            unrelatedTransition,
            ScrollCaptureDirection.Up,
            options,
            expectedNewRows: 43,
            lockDirection: true,
            out _));

        var originalHeight = composer.OutputHeight;
        foreach (var shift in new[] { 27, 61, 80, 125 })
        {
            using var settlingFrame = ShiftViewportDown(initial, shift);
            Assert.False(composer.TryAddFrame(
                settlingFrame,
                ScrollCaptureDirection.Up,
                options,
                expectedNewRows: 43,
                lockDirection: true,
                out _));
            Assert.Equal("return-boundary-gap-veto", composer.LastRejectReason);
            Assert.Equal(originalHeight, composer.OutputHeight);
            Assert.Equal(0, composer.CapturedContentTop);
        }

        using var result = composer.Compose();
        Assert.Equal(originalHeight, result.Height);
    }

    [Fact]
    public void ReturningToCapturedTopDoesNotPrependSettlingFramesWithoutAGap()
    {
        const int width = 320;
        const int height = 240;
        const int downTop = 180;
        var options = Options with
        {
            MinimumOverlapRows = 24,
            MinimumOverlapConfidence = 0.96,
        };
        using var document = CreateCodeEditorContent(width, downTop + height + 20);
        using var initial = Crop(document, 0, height);
        using var down = Crop(document, downTop, height);
        using var composer = new ScrollCaptureComposer();

        Assert.True(composer.TryAddFrame(initial, options, out _));
        Assert.True(composer.TryAddFrame(
            down,
            ScrollCaptureDirection.Down,
            options,
            expectedNewRows: downTop,
            lockDirection: true,
            out _));
        Assert.False(composer.TryAddFrame(
            initial,
            ScrollCaptureDirection.Up,
            options,
            expectedNewRows: downTop,
            lockDirection: true,
            out _));

        var originalHeight = composer.OutputHeight;
        // This frame has a clean, high-confidence overlap with the initial
        // viewport, but is only a top-layout transition (the same shape seen
        // in the live report).  There is no unmatched gap before it.
        using var firstSettlingFrame = ShiftViewportDown(initial, 75);
        Assert.False(composer.TryAddFrame(
            firstSettlingFrame,
            ScrollCaptureDirection.Up,
            options,
            expectedNewRows: 56,
            lockDirection: true,
            out _));
        Assert.Equal("return-boundary-settle-veto", composer.LastRejectReason);

        foreach (var shift in new[] { 131, 42, 187 })
        {
            using var settlingFrame = ShiftViewportDown(initial, shift);
            Assert.False(composer.TryAddFrame(
                settlingFrame,
                ScrollCaptureDirection.Up,
                options,
                expectedNewRows: null,
                lockDirection: true,
                out _));
            Assert.Equal(originalHeight, composer.OutputHeight);
            Assert.Equal(0, composer.CapturedContentTop);
        }
    }

    [Fact]
    public void StationaryReturnedTopDoesNotPrependDelayedDuplicateHeaderFrames()
    {
        const int width = 320;
        const int height = 240;
        const int downTop = 180;
        var options = Options with
        {
            MinimumOverlapRows = 24,
            MinimumOverlapConfidence = 0.96,
        };
        using var document = CreateCodeEditorContent(
            width,
            downTop + height + 20);
        using var initial = Crop(document, 0, height);
        using var down = Crop(document, downTop, height);
        using var composer = new ScrollCaptureComposer();

        Assert.True(composer.TryAddFrame(initial, options, out _));
        Assert.True(composer.TryAddFrame(
            down,
            ScrollCaptureDirection.Down,
            options,
            expectedNewRows: downTop,
            lockDirection: true,
            out _));
        Assert.False(composer.TryAddFrame(
            initial,
            ScrollCaptureDirection.Up,
            options,
            expectedNewRows: downTop,
            lockDirection: true,
            out _));

        // The live trace contained one more identical sample at top before a
        // queued wheel tick was paired with a 10 px compositor transition.
        Assert.False(composer.TryAddFrame(
            initial,
            ScrollCaptureDirection.Up,
            options,
            expectedNewRows: null,
            lockDirection: false,
            out _));

        var originalHeight = composer.OutputHeight;
        using var delayedTenPixelFrame = PrefixDuplicateAndShiftDown(
            initial,
            10);
        Assert.False(composer.TryAddFrame(
            delayedTenPixelFrame,
            ScrollCaptureDirection.Up,
            options,
            expectedNewRows: 34,
            lockDirection: true,
            out _));
        Assert.True(
            composer.LastRejectReason is
                "return-boundary-stationary-duplicate-veto" or
                "return-boundary-settle-veto");

        // Further same-direction wheel input and inertia still belong to the
        // same physical-top settle. They must not clear the visual boundary.
        using var delayedSeventyPixelFrame = PrefixDuplicateAndShiftDown(
            initial,
            70);
        Assert.False(composer.TryAddFrame(
            delayedSeventyPixelFrame,
            ScrollCaptureDirection.Up,
            options,
            expectedNewRows: 32,
            lockDirection: true,
            out _));
        Assert.True(
            composer.LastRejectReason is
                "return-boundary-gap-veto" or
                "return-boundary-settle-veto");
        Assert.Equal(originalHeight, composer.OutputHeight);
        Assert.Equal(0, composer.CapturedContentTop);
    }

    [Fact]
    public void KeepsEveryRowContiguousAcrossDownUpDownFrameStitching()
    {
        using var initialFrame = CreateFrame(startY: 200, width: 96, height: 100);
        using var firstDownwardFrame = CreateFrame(
            startY: 240,
            width: 96,
            height: 100);
        using var secondDownwardFrame = CreateFrame(
            startY: 280,
            width: 96,
            height: 100);
        using var returnedFrame = CreateFrame(startY: 240, width: 96, height: 100);
        using var upwardFrame = CreateFrame(startY: 160, width: 96, height: 100);
        using var returnedDownwardFrame = CreateFrame(
            startY: 240,
            width: 96,
            height: 100);
        using var finalDownwardFrame = CreateFrame(startY: 320, width: 96, height: 100);
        using var composer = new ScrollCaptureComposer();

        Assert.True(composer.TryAddFrame(initialFrame, Options, out _));
        Assert.True(composer.TryAddFrame(
            firstDownwardFrame,
            ScrollCaptureDirection.Down,
            Options,
            out _));
        Assert.True(composer.TryAddFrame(
            secondDownwardFrame,
            ScrollCaptureDirection.Down,
            Options,
            out _));
        Assert.False(composer.TryAddFrame(
            returnedFrame,
            ScrollCaptureDirection.Up,
            Options,
            out _));
        Assert.True(composer.TryAddFrame(
            upwardFrame,
            ScrollCaptureDirection.Up,
            Options,
            out _));
        Assert.False(composer.TryAddFrame(
            returnedDownwardFrame,
            ScrollCaptureDirection.Down,
            Options,
            out _));
        Assert.True(composer.TryAddFrame(
            finalDownwardFrame,
            ScrollCaptureDirection.Down,
            Options,
            out _));

        using var result = composer.Compose();

        Assert.Equal(260, result.Height);

        for (var y = 0; y < result.Height; y++)
        {
            Assert.Equal(ExpectedColor(15, y + 160), result.GetPixel(15, y));
        }
    }

    [Fact]
    public void DoesNotReappendMiddleCodeRowsAfterScrollingBackFromTheBottom()
    {
        const int width = 320;
        const int height = 288;
        const int scrollDistance = 36;
        var codeOptions = Options with
        {
            MaximumFrames = 40,
            MinimumOverlapRows = 24,
            MinimumOverlapConfidence = 0.96,
        };
        // A tall, highly repetitive code document. Content taller than the
        // captured range guarantees a genuine bottom that the user can reach and
        // then scroll back up from.
        var contentHeight = height + (scrollDistance * 6);
        using var content = CreateCodeEditorContent(width, contentHeight);
        using var composer = new ScrollCaptureComposer();

        var lastFrameTop = contentHeight - height;

        // Scroll all the way down, capturing every viewport to the bottom.
        for (var top = 0; top <= lastFrameTop; top += scrollDistance)
        {
            using var frame = Crop(content, top, height);
            composer.TryAddFrame(frame, ScrollCaptureDirection.Down, codeOptions, out _);
        }

        var heightAtBottom = composer.OutputHeight;
        var framesAtBottom = composer.FrameCount;

        // Scroll back up to the middle. These viewports were already captured on
        // the way down, so they must not be appended as if they were new bottom
        // content. Real captures of a returned viewport are never pixel-identical
        // to what was stored: a blinking caret, hover state or a moved scrollbar
        // thumb perturbs a few pixels. Simulate that so the de-duplication path is
        // exercised the way it is in production, not on an idealized exact frame.
        for (var top = lastFrameTop - scrollDistance;
             top >= lastFrameTop / 2;
             top -= scrollDistance)
        {
            using var frame = Crop(content, top, height);
            using (var perturbation = Graphics.FromImage(frame))
            using (var caretBrush = new SolidBrush(Color.FromArgb(255, 220, 220, 220)))
            {
                // A one-pixel-wide caret near the top-left, like a text cursor.
                perturbation.FillRectangle(caretBrush, 70, 6, 1, 12);
            }

            composer.TryAddFrame(frame, ScrollCaptureDirection.Up, codeOptions, out _);
        }

        Assert.Equal(heightAtBottom, composer.OutputHeight);
        Assert.Equal(framesAtBottom, composer.FrameCount);
        Assert.Equal(contentHeight, composer.OutputHeight);

        using var result = composer.Compose();

        // Every stitched row must line up with the original document — no
        // duplicated or torn middle rows at the bottom.
        for (var y = 0; y < result.Height; y += 3)
        {
            for (var x = 0; x < result.Width; x += 7)
            {
                Assert.Equal(content.GetPixel(x, y), result.GetPixel(x, y));
            }
        }
    }

    [Fact]
    public void KeepsVariableCodeScrollsContinuousAcrossAReversal()
    {
        const int width = 320;
        const int height = 288;
        var tops = new[] { 0, 73, 151, 222, 151, 73, 151, 222 };
        var directions = new[]
        {
            ScrollCaptureDirection.Down,
            ScrollCaptureDirection.Down,
            ScrollCaptureDirection.Down,
            ScrollCaptureDirection.Down,
            ScrollCaptureDirection.Up,
            ScrollCaptureDirection.Up,
            ScrollCaptureDirection.Down,
            ScrollCaptureDirection.Down,
        };
        var options = Options with
        {
            MaximumFrames = 40,
            MinimumOverlapRows = 24,
            MinimumOverlapConfidence = 0.96,
        };
        using var content = CreateCodeEditorContent(width, height + 222);
        using var composer = new ScrollCaptureComposer();

        for (var index = 0; index < tops.Length; index++)
        {
            using var frame = Crop(content, tops[index], height);
            composer.TryAddFrame(frame, directions[index], options, out _);
        }

        Assert.Equal(height + 222, composer.OutputHeight);
        using var result = composer.Compose();

        for (var y = 0; y < result.Height; y += 3)
        {
            for (var x = 0; x < result.Width; x += 7)
            {
                Assert.Equal(content.GetPixel(x, y), result.GetPixel(x, y));
            }
        }
    }

    [Fact]
    public void PrefersTemporallyConsistentDisplacementOnPeriodicContent()
    {
        // A viewport made of identical horizontal bands every 40px. Without a
        // temporal prior the matcher can latch onto any multiple of the period.
        // Continuous scrolling should keep choosing the same step size.
        const int width = 180;
        const int height = 200;
        const int period = 40;
        const int scrollDistance = 40;
        using var content = CreatePeriodicBandContent(width, height + (scrollDistance * 5), period);
        using var first = Crop(content, 0, height);
        using var second = Crop(content, scrollDistance, height);
        using var third = Crop(content, scrollDistance * 2, height);

        var firstMatch = ImageOverlapMatcher.FindVerticalOverlap(
            first,
            second,
            minimumOverlapRows: 24,
            minimumConfidence: 0.96,
            minimumNewRows: 8);
        Assert.NotNull(firstMatch);
        Assert.Equal(height - scrollDistance, firstMatch.OverlapRows);

        var preferredNewRows = height - firstMatch.OverlapRows;
        var secondMatch = ImageOverlapMatcher.FindVerticalOverlap(
            second,
            third,
            minimumOverlapRows: 24,
            minimumConfidence: 0.96,
            minimumNewRows: 8,
            preferredNewRows: preferredNewRows);

        Assert.NotNull(secondMatch);
        Assert.Equal(height - scrollDistance, secondMatch.OverlapRows);
    }

    [Fact]
    public void StitchesManySmallContinuousScrollsWithoutGapsOrDuplicates()
    {
        const int width = 240;
        const int height = 220;
        // Non-uniform step sizes emulate real mouse-wheel + inertia mixes.
        var tops = new[] { 0, 17, 41, 68, 92, 121, 149, 178, 210 };
        var options = Options with
        {
            MaximumFrames = 40,
            MinimumOverlapRows = 24,
            MinimumOverlapConfidence = 0.96,
        };
        using var content = CreateCodeEditorContent(width, height + tops[^1]);
        using var composer = new ScrollCaptureComposer();

        foreach (var top in tops)
        {
            using var frame = Crop(content, top, height);
            composer.TryAddFrame(frame, ScrollCaptureDirection.Down, options, out _);
        }

        Assert.Equal(height + tops[^1], composer.OutputHeight);
        using var result = composer.Compose();

        for (var y = 0; y < result.Height; y += 2)
        {
            for (var x = 0; x < result.Width; x += 5)
            {
                Assert.Equal(content.GetPixel(x, y), result.GetPixel(x, y));
            }
        }
    }

    [Fact]
    public void StitchesTallFastCodeFramesSeenOnlyOnce()
    {
        const int width = 640;
        const int height = 720;
        var tops = new[] { 0, 72, 162, 270, 396, 540, 702, 882 };
        var options = Options with
        {
            MaximumFrames = 30,
            MinimumOverlapRows = 20,
            MinimumOverlapConfidence = 0.93,
            MinimumNewRows = 4,
        };
        using var content = CreateCodeEditorContent(
            width,
            height + tops[^1]);
        using var composer = new ScrollCaptureComposer();

        foreach (var top in tops)
        {
            using var frame = Crop(content, top, height);
            Assert.True(
                composer.TryAddFrame(
                    frame,
                    ScrollCaptureDirection.Down,
                    options,
                    out _),
                $"failed at top={top}");
        }

        Assert.Equal(height + tops[^1], composer.OutputHeight);

        using var result = composer.Compose();
        for (var y = 0; y < result.Height; y += 3)
        {
            for (var x = 0; x < result.Width; x += 9)
            {
                Assert.Equal(content.GetPixel(x, y), result.GetPixel(x, y));
            }
        }
    }

    [Fact]
    public void FollowsLargeStepChangeAfterSmallContinuousScrolls()
    {
        // After several small steps the temporal prior is strong. A later larger
        // wheel jump must still win when the image evidence is clear — otherwise
        // the stitch freezes on the old step size.
        const int width = 200;
        const int height = 240;
        var tops = new[] { 0, 28, 56, 84, 160 };
        var options = Options with
        {
            MaximumFrames = 20,
            MinimumOverlapRows = 24,
            MinimumOverlapConfidence = 0.96,
        };
        using var content = CreateCodeEditorContent(width, height + tops[^1]);
        using var composer = new ScrollCaptureComposer();

        foreach (var top in tops)
        {
            using var frame = Crop(content, top, height);
            Assert.True(
                composer.TryAddFrame(frame, ScrollCaptureDirection.Down, options, out _),
                $"failed at top={top}");
        }

        Assert.Equal(height + tops[^1], composer.OutputHeight);
        using var result = composer.Compose();
        for (var y = 0; y < result.Height; y += 4)
        {
            Assert.Equal(content.GetPixel(40, y), result.GetPixel(40, y));
        }
    }

    [Fact]
    public void AppliesHorizontalMicroAlignmentWhenComposingShiftedFrames()
    {
        // WeChat-like capture can drift by 1px horizontally across frames
        // (DWM / window chrome). Matching already recovers HorizontalOffset;
        // compose must apply it or the stitch shows a jagged vertical seam.
        const int width = 160;
        const int height = 140;
        const int scroll = 36;
        const int shift = 1;
        var options = Options with
        {
            MaximumFrames = 8,
            MinimumOverlapRows = 24,
            MinimumOverlapConfidence = 0.96,
            MinimumNewRows = 8,
        };

        using var content = CreateGradientContent(width + shift, height + scroll);
        using var firstFrame = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
        using var secondFrame = new Bitmap(width, height, PixelFormat.Format32bppPArgb);

        // first frame: content columns [0, width)
        // second frame: content columns [shift, width+shift) at y+scroll — a 1px
        // horizontal compositor drift relative to the first frame.
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                firstFrame.SetPixel(x, y, content.GetPixel(x, y));
                secondFrame.SetPixel(x, y, content.GetPixel(x + shift, y + scroll));
            }
        }

        using var composer = new ScrollCaptureComposer();
        Assert.True(composer.TryAddFrame(firstFrame, options, out _));
        Assert.True(composer.TryAddFrame(secondFrame, options, out var match));
        Assert.NotNull(match);
        Assert.Equal(height - scroll, match!.OverlapRows);
        Assert.Equal(-shift, match.HorizontalOffset);
        Assert.Equal(height + scroll, composer.OutputHeight);

        using var result = composer.Compose();
        // Top region comes from the unshifted first frame.
        for (var y = 0; y < height - 4; y += 3)
        {
            for (var x = 0; x < width - shift; x += 5)
            {
                Assert.Equal(content.GetPixel(x, y), result.GetPixel(x, y));
            }
        }

        // Newly appended rows should be horizontally remapped back into the
        // composite basis (content x, not the shifted frame x). The far edge
        // column exposed by the shift is edge-filled (source frame never saw
        // content[x=0] after a +1 compositor drift), so skip that gutter.
        for (var y = height; y < result.Height; y += 2)
        {
            for (var x = shift; x < width - shift; x += 4)
            {
                Assert.Equal(content.GetPixel(x, y), result.GetPixel(x, y));
            }
        }
    }

    [Fact]
    public void StitchesWeChatLikeChatBubblesWithoutDuplication()
    {
        const int width = 280;
        const int height = 320;
        var tops = new[] { 0, 36, 72, 118, 154, 190, 240 };
        var options = Options with
        {
            MaximumFrames = 30,
            MinimumOverlapRows = 24,
            MinimumOverlapConfidence = 0.96,
        };
        using var content = CreateWeChatLikeChatContent(width, height + tops[^1]);
        using var composer = new ScrollCaptureComposer();

        foreach (var top in tops)
        {
            using var frame = Crop(content, top, height);
            composer.TryAddFrame(frame, ScrollCaptureDirection.Down, options, out _);
        }

        Assert.Equal(height + tops[^1], composer.OutputHeight);
        using var result = composer.Compose();

        for (var y = 0; y < result.Height; y += 3)
        {
            for (var x = 0; x < result.Width; x += 9)
            {
                Assert.Equal(content.GetPixel(x, y), result.GetPixel(x, y));
            }
        }
    }


    [Fact]
    public void TemporalPriorResolvesContinuousScrollsWithinTightBudget()
    {
        const int width = 360;
        const int height = 280;
        const int scrollDistance = 36;
        using var content = CreateGradientContent(width, height + (scrollDistance * 4));
        using var previousFrame = Crop(content, scrollDistance, height);
        using var currentFrame = Crop(content, scrollDistance * 2, height);

        var cold = ImageOverlapMatcher.FindVerticalOverlap(
            previousFrame,
            currentFrame,
            minimumOverlapRows: 24,
            minimumConfidence: 0.96,
            minimumNewRows: 8);
        Assert.NotNull(cold);
        Assert.Equal(height - scrollDistance, cold.OverlapRows);

        var stopwatch = Stopwatch.StartNew();
        var warm = ImageOverlapMatcher.FindVerticalOverlap(
            previousFrame,
            currentFrame,
            minimumOverlapRows: 24,
            minimumConfidence: 0.96,
            minimumNewRows: 8,
            preferredNewRows: scrollDistance);
        stopwatch.Stop();

        Assert.NotNull(warm);
        Assert.Equal(height - scrollDistance, warm.OverlapRows);
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromMilliseconds(250),
            $"时间先验匹配耗时 {stopwatch.Elapsed.TotalMilliseconds:F0}ms。");
    }

    [Fact]
    public void TemporalPriorDoesNotTrapALargeStepChange()
    {
        const int width = 280;
        const int height = 240;
        const int smallStep = 20;
        const int largeStep = 96;
        using var content = CreateGradientContent(width, height + smallStep + largeStep + 40);
        using var previousFrame = Crop(content, smallStep, height);
        using var currentFrame = Crop(content, smallStep + largeStep, height);

        var match = ImageOverlapMatcher.FindVerticalOverlap(
            previousFrame,
            currentFrame,
            minimumOverlapRows: 24,
            minimumConfidence: 0.96,
            minimumNewRows: 8,
            preferredNewRows: smallStep);

        Assert.NotNull(match);
        Assert.Equal(height - largeStep, match.OverlapRows);
    }
    [Fact]
    public void ExcludesFixedBottomChromeAcrossDownUpAndAboveStartScrolling()
    {
        const int width = 360;
        const int frameHeight = 307;
        const int fixedBottomRows = 18;
        const int scrollableHeight = frameHeight - fixedBottomRows;
        const int initialTop = 240;
        var tops = new[] { initialTop, 300, 310, 360, 300, initialTop, 180 };
        var directions = new[]
        {
            ScrollCaptureDirection.Down,
            ScrollCaptureDirection.Down,
            ScrollCaptureDirection.Down,
            ScrollCaptureDirection.Up,
            ScrollCaptureDirection.Up,
            ScrollCaptureDirection.Up,
        };
        var options = Options with
        {
            MaximumFrames = 20,
            MinimumOverlapRows = 24,
            MinimumOverlapConfidence = 0.96,
        };
        var expectedTop = tops.Min();
        var expectedBottom = tops.Max() + scrollableHeight;
        using var document = CreateCodeEditorContent(
            width,
            expectedBottom + 40);
        using var composer = new ScrollCaptureComposer();

        using (var initial = CreateFrameWithFixedBottomChrome(
                   document,
                   tops[0],
                   frameHeight,
                   fixedBottomRows))
        {
            Assert.True(composer.TryAddFrame(initial, options, out _));
        }

        for (var index = 1; index < tops.Length; index++)
        {
            using var frame = CreateFrameWithFixedBottomChrome(
                document,
                tops[index],
                frameHeight,
                fixedBottomRows);
            _ = composer.TryAddFrame(
                frame,
                directions[index - 1],
                options,
                expectedNewRows: Math.Abs(tops[index] - tops[index - 1]),
                lockDirection: true,
                out _);

            // A pause produces repeated frames in the live queue. It must not
            // write the fixed horizontal scrollbar or duplicate code rows.
            _ = composer.TryAddFrame(
                frame,
                directions[index - 1],
                options,
                expectedNewRows: null,
                lockDirection: false,
                out _);
        }

        Assert.Equal(fixedBottomRows, composer.FixedBottomRows);
        using var result = composer.Compose();
        using var expected = document.Clone(
            new Rectangle(
                0,
                expectedTop,
                width,
                expectedBottom - expectedTop),
            PixelFormat.Format32bppPArgb);
        Assert.Equal(expected.Size, result.Size);
        for (var y = 0; y < expected.Height; y++)
        {
            for (var x = 0; x < expected.Width; x++)
            {
                Assert.Equal(expected.GetPixel(x, y), result.GetPixel(x, y));
            }
        }
    }

    private static Bitmap CreateWeChatLikeChatContent(int width, int height)
    {
        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.FromArgb(255, 237, 237, 237));
        graphics.SmoothingMode = SmoothingMode.None;

        // Fixed-ish top title bar texture (partial sticky feel when cropped mid-scroll).
        using (var title = new SolidBrush(Color.FromArgb(255, 246, 246, 246)))
        {
            graphics.FillRectangle(title, 0, 0, width, 36);
        }

        for (var message = 0; message * 56 < height; message++)
        {
            var top = 40 + (message * 56);
            var isSelf = message % 3 == 0;
            var avatarX = isSelf ? width - 44 : 12;
            var bubbleX = isSelf ? width - 200 : 56;
            var avatarColor = Color.FromArgb(
                255,
                40 + ((message * 37) % 160),
                80 + ((message * 53) % 120),
                100 + ((message * 19) % 100));
            var bubbleColor = isSelf
                ? Color.FromArgb(255, 149, 236, 105)
                : Color.FromArgb(255, 255, 255, 255);

            using (var avatarBrush = new SolidBrush(avatarColor))
            {
                graphics.FillRectangle(avatarBrush, avatarX, top, 32, 32);
            }

            using (var bubbleBrush = new SolidBrush(bubbleColor))
            {
                graphics.FillRectangle(bubbleBrush, bubbleX, top + 2, 140, 36);
            }

            // Repeated short text strokes inside bubbles — the failure mode WeChat
            // stitchers must not latch onto when two messages look similar.
            using (var textPen = new Pen(Color.FromArgb(255, 60, 60, 60), 2))
            {
                for (var line = 0; line < 3; line++)
                {
                    var y = top + 8 + (line * 8);
                    graphics.DrawLine(textPen, bubbleX + 10, y, bubbleX + 110 - (line * 12), y);
                }
            }
        }

        return bitmap;
    }
    private static Bitmap CreatePeriodicBandContent(int width, int height, int period)
    {
        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.FromArgb(255, 245, 245, 245));

        for (var y = 0; y < height; y++)
        {
            var band = (y / period) % 4;
            var color = band switch
            {
                0 => Color.FromArgb(255, 48, 52, 64),
                1 => Color.FromArgb(255, 86, 120, 180),
                2 => Color.FromArgb(255, 210, 176, 96),
                _ => Color.FromArgb(255, 132, 92, 148),
            };
            using var pen = new Pen(color);
            graphics.DrawLine(pen, 0, y, width - 12, y);
        }

        // Stable left gutter so sparse-anchor logic has texture without breaking
        // the periodic body.
        using var gutter = new SolidBrush(Color.FromArgb(255, 28, 30, 36));
        graphics.FillRectangle(gutter, 0, 0, 28, height);

        for (var y = 0; y < height; y += 12)
        {
            using var mark = new SolidBrush(Color.FromArgb(255, 180, 180, 185));
            graphics.FillRectangle(mark, 6, y + 3, 14, 5);
        }

        return bitmap;
    }


    private static Bitmap CreateGradientContent(int width, int height)
    {
        var content = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                content.SetPixel(x, y, ExpectedColor(x, y));
            }
        }

        return content;
    }
    /// <summary>
    /// A frame whose top <paramref name="staleRows"/> rows carry a different
    /// texture family than <see cref="CreateFrame"/> produces for the same
    /// document rows — simulating a page that repainted that band after the
    /// frame was stored (live timestamps, hover state, streaming indicators).
    /// </summary>
    private static Bitmap CreateFrameWithRepaintedBand(
        int startY,
        int width,
        int height,
        int staleRows)
    {
        var frame = CreateFrame(startY, width, height);
        for (var y = 0; y < staleRows; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var documentY = startY + y;
                frame.SetPixel(x, y, Color.FromArgb(
                    255,
                    (x * 53 + documentY * 97) & 0xff,
                    (x * 11 + documentY * 3) & 0xff,
                    (x * 29 + documentY * 61) & 0xff));
            }
        }

        return frame;
    }

    private static Bitmap CreateFrame(int startY, int width, int height)
    {
        var frame = new Bitmap(width, height, PixelFormat.Format32bppPArgb);

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                frame.SetPixel(x, y, ExpectedColor(x, startY + y));
            }
        }

        return frame;
    }

    private static Bitmap CreateSolidFrame(Color color, int width, int height)
    {
        var frame = new Bitmap(width, height, PixelFormat.Format32bppPArgb);

        using var graphics = Graphics.FromImage(frame);
        graphics.Clear(color);
        return frame;
    }

    private static Bitmap CreateRepeatingFrame(
        int width,
        int height,
        Color scrollbarColor)
    {
        const int period = 20;
        var frame = new Bitmap(width, height, PixelFormat.Format32bppPArgb);

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                frame.SetPixel(x, y, ExpectedColor(x, y % period));
            }
        }

        using var graphics = Graphics.FromImage(frame);
        using var scrollbarBrush = new SolidBrush(scrollbarColor);
        graphics.FillRectangle(
            scrollbarBrush,
            width - 10,
            0,
            10,
            height);
        return frame;
    }

    private static Bitmap CreateBrowserLikeFrame(
        int startY,
        int width,
        int height,
        int headerRows,
        Color scrollbarColor)
    {
        var frame = new Bitmap(width, height, PixelFormat.Format32bppPArgb);

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var color = y < headerRows
                    ? Color.FromArgb(
                        255,
                        (x * 11 + y * 3) & 0xff,
                        (x * 5 + 41) & 0xff,
                        (y * 17 + 73) & 0xff)
                    : ExpectedBrowserColor(x, startY + y - headerRows);

                if (x >= width - 10)
                {
                    color = scrollbarColor;
                }

                frame.SetPixel(x, y, color);
            }
        }

        return frame;
    }

    private static Bitmap ShiftViewportDown(Bitmap source, int rows)
    {
        var shift = Math.Clamp(rows, 1, source.Height - 1);
        var frame = new Bitmap(
            source.Width,
            source.Height,
            PixelFormat.Format32bppPArgb);
        using var graphics = Graphics.FromImage(frame);
        graphics.Clear(Color.White);
        using var markerBrush = new SolidBrush(Color.FromArgb(255, 31, 63, 95));
        graphics.FillRectangle(markerBrush, 0, 0, source.Width, shift);
        graphics.DrawImageUnscaled(source, 0, shift);
        return frame;
    }

    private static Bitmap PrefixDuplicateAndShiftDown(Bitmap source, int rows)
    {
        var shift = Math.Clamp(rows, 1, source.Height - 1);
        var frame = new Bitmap(
            source.Width,
            source.Height,
            PixelFormat.Format32bppPArgb);
        using var graphics = Graphics.FromImage(frame);
        graphics.DrawImage(
            source,
            new Rectangle(0, 0, source.Width, shift),
            new Rectangle(0, 0, source.Width, shift),
            GraphicsUnit.Pixel);
        graphics.DrawImage(
            source,
            new Rectangle(
                0,
                shift,
                source.Width,
                source.Height - shift),
            new Rectangle(
                0,
                0,
                source.Width,
                source.Height - shift),
            GraphicsUnit.Pixel);
        return frame;
    }

    private static Bitmap CreateStickyDashboardFrame(
        int documentTop,
        int width,
        int height,
        int stickyRows,
        int updateSeed)
    {
        var frame = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
        using var graphics = Graphics.FromImage(frame);
        graphics.Clear(Color.FromArgb(255, 8, 20, 38));

        using (var headerBrush = new SolidBrush(Color.FromArgb(255, 18, 36, 58)))
        using (var controlBrush = new SolidBrush(Color.FromArgb(255, 29, 49, 76)))
        using (var accentBrush = new SolidBrush(Color.FromArgb(255, 22, 190, 177)))
        {
            graphics.FillRectangle(headerBrush, 0, 0, width, stickyRows);
            graphics.FillRectangle(controlBrush, 12, 11, 82, 28);
            graphics.FillRectangle(controlBrush, 104, 11, 82, 28);
            graphics.FillRectangle(controlBrush, 196, 11, 82, 28);
            graphics.FillRectangle(accentBrush, 292, 11, 54, 28);
        }

        // Live totals change while the same viewport is revisited. Keep these
        // updates away from the left structural anchor, as in the dashboard
        // from the reported capture.
        using (var dynamicBrush = new SolidBrush(Color.FromArgb(
                   255,
                   45 + ((updateSeed * 31) % 140),
                   70 + ((updateSeed * 47) % 120),
                   100 + ((updateSeed * 19) % 110))))
        {
            graphics.FillRectangle(dynamicBrush, 218, 17, 52, 15);
            graphics.FillRectangle(dynamicBrush, 310, 17, 24, 15);
        }

        for (var y = stickyRows; y < height; y++)
        {
            var documentY = documentTop + y - stickyRows;
            var row = documentY / 44;
            var withinRow = documentY % 44;
            var background = row % 2 == 0
                ? Color.FromArgb(255, 12, 28, 50)
                : Color.FromArgb(255, 15, 32, 55);
            using var backgroundBrush = new SolidBrush(background);
            graphics.FillRectangle(backgroundBrush, 0, y, width, 1);

            if (withinRow is 0 or 1)
            {
                using var separator = new Pen(Color.FromArgb(255, 40, 58, 82));
                graphics.DrawLine(separator, 0, y, width - 1, y);
            }

            if (withinRow >= 13 && withinRow <= 16)
            {
                using var textBrush = new SolidBrush(Color.FromArgb(
                    255,
                    90 + ((row * 17) % 120),
                    130 + ((row * 23) % 100),
                    150 + ((row * 11) % 90)));
                graphics.FillRectangle(textBrush, 14, y, 38 + ((row * 7) % 28), 1);
                graphics.FillRectangle(textBrush, 112, y, 58, 1);
                graphics.FillRectangle(textBrush, 224, y, 72, 1);
            }
        }

        using (var liveCellBrush = new SolidBrush(Color.FromArgb(
                   255,
                   45 + ((updateSeed * 31) % 140),
                   70 + ((updateSeed * 47) % 120),
                   100 + ((updateSeed * 19) % 110))))
        {
            graphics.FillRectangle(
                liveCellBrush,
                width / 2,
                stickyRows + 34,
                width / 3,
                18);
        }

        using (var scrollbarBrush = new SolidBrush(Color.FromArgb(
                   255,
                   80 + ((updateSeed * 29) % 130),
                   100,
                   125)))
        {
            var thumbTop = stickyRows + (documentTop % Math.Max(1, height - stickyRows - 30));
            graphics.FillRectangle(scrollbarBrush, width - 8, thumbTop, 5, 28);
        }

        return frame;
    }

    private static Bitmap CreateSparseChatContent(int width, int height)
    {
        var content = new Bitmap(width, height, PixelFormat.Format32bppPArgb);

        using var graphics = Graphics.FromImage(content);
        graphics.Clear(Color.FromArgb(255, 31, 31, 31));
        var messageTops = new[] { 12, 64, 122, 188, 248, 310 };

        for (var index = 0; index < messageTops.Length; index++)
        {
            var messageTop = messageTops[index];
            var isOutgoing = index % 2 == 1;
            var avatarLeft = isOutgoing ? width - 30 : 12;
            var bubbleWidth = 68 + ((index * 17) % 54);
            var bubbleHeight = 30 + ((index % 3) * 12);
            var bubbleLeft = isOutgoing
                ? avatarLeft - bubbleWidth - 8
                : avatarLeft + 30;
            var bubbleColor = isOutgoing
                ? Color.FromArgb(255, 46, 204, 142)
                : Color.FromArgb(255, 60, 60, 62);
            var markerColor = Color.FromArgb(
                255,
                (index * 53 + 91) & 0xff,
                (index * 89 + 47) & 0xff,
                (index * 29 + 151) & 0xff);

            using var avatarBrush = new SolidBrush(Color.FromArgb(255, 175, 175, 175));
            using var bubbleBrush = new SolidBrush(bubbleColor);
            using var markerBrush = new SolidBrush(markerColor);
            using var textBrush = new SolidBrush(Color.FromArgb(255, 226, 226, 226));
            graphics.FillRectangle(avatarBrush, avatarLeft, messageTop, 20, 20);
            graphics.FillRectangle(markerBrush, avatarLeft + 4, messageTop + 4, 12, 12);
            graphics.FillRectangle(
                bubbleBrush,
                bubbleLeft,
                messageTop,
                bubbleWidth,
                bubbleHeight);

            for (var line = 0; line < 3; line++)
            {
                var lineWidth = bubbleWidth - 20 - ((line * 11 + index * 7) % 22);
                graphics.FillRectangle(
                    textBrush,
                    bubbleLeft + 10,
                    messageTop + 8 + (line * 8),
                    lineWidth,
                    3);
            }
        }

        return content;
    }

    [Fact]
    public void ReanchorsALostFlingAtThePhysicalTopBoundary()
    {
        const int width = 320;
        const int height = 260;
        var options = Options with
        {
            MaximumFrames = 20,
            MinimumOverlapRows = 24,
            MinimumOverlapConfidence = 0.96,
        };
        using var content = new Bitmap(width, 900, PixelFormat.Format32bppPArgb);
        for (var y = 0; y < content.Height; y++)
        {
            for (var x = 0; x < content.Width; x++)
            {
                content.SetPixel(x, y, ExpectedBrowserColor(x, y));
            }
        }

        using var composer = new ScrollCaptureComposer();

        // Capture starts 300 rows below the page top and scrolls down.
        foreach (var top in new[] { 300, 400, 500 })
        {
            using var frame = Crop(content, top, height);
            Assert.True(
                composer.TryAddFrame(
                    frame,
                    ScrollCaptureDirection.Down,
                    options,
                    out _),
                $"failed at top={top}");
        }

        // A virtualized fling jumps the page to its physical top without any
        // located samples in between; the anchor is now stale. The boundary
        // marker path hands the stationary top frame to the re-anchor.
        using var physicalTopFrame = Crop(content, 100, height);
        Assert.False(
            composer.TryMarkBoundaryReached(
                physicalTopFrame,
                ScrollCaptureDirection.Up));
        Assert.True(
            composer.TryReanchorAtBoundary(
                physicalTopFrame,
                ScrollCaptureDirection.Up,
                options));

        // 200 missing rows above the old start must now be in the output.
        Assert.Equal(1, composer.AddedAboveFrameCount);
        Assert.Equal(height + 200 + 200, composer.OutputHeight);

        using var result = composer.Compose();
        for (var y = 0; y < result.Height; y += 3)
        {
            for (var x = 0; x < result.Width; x += 7)
            {
                Assert.Equal(
                    content.GetPixel(x, y + 100),
                    result.GetPixel(x, y));
            }
        }
    }

    private static Bitmap CreateCodeEditorContent(int width, int height)
    {
        var content = new Bitmap(width, height, PixelFormat.Format32bppPArgb);

        using var graphics = Graphics.FromImage(content);
        graphics.Clear(Color.FromArgb(255, 30, 34, 39));
        using var gutterBrush = new SolidBrush(Color.FromArgb(255, 35, 40, 46));
        using var codeBrush = new SolidBrush(Color.FromArgb(255, 104, 170, 212));
        using var punctuationBrush = new SolidBrush(Color.FromArgb(255, 213, 149, 98));
        using var numberBrush = new SolidBrush(Color.FromArgb(255, 82, 99, 116));
        const int lineHeight = 18;
        const int gutterWidth = 54;

        for (var line = 0; line * lineHeight < height; line++)
        {
            var top = line * lineHeight;
            graphics.FillRectangle(gutterBrush, 0, top, gutterWidth, lineHeight - 1);

            // Encode the line number as stable bars in the gutter. The body is
            // intentionally repetitive so the gutter is the useful anchor.
            for (var bit = 0; bit < 10; bit++)
            {
                if (((line + 1) & (1 << bit)) == 0)
                {
                    continue;
                }

                graphics.FillRectangle(
                    numberBrush,
                    5 + (bit * 4),
                    top + 4,
                    3,
                    10);
            }

            for (var token = 0; token < 7; token++)
            {
                var tokenLeft = gutterWidth + 16 + (token * 31);
                graphics.FillRectangle(
                    token % 3 == 0 ? punctuationBrush : codeBrush,
                    tokenLeft,
                    top + 5,
                    20 + ((token % 2) * 8),
                    3);
            }
        }

        return content;
    }

    private static Bitmap CreateFrameWithFixedBottomChrome(
        Bitmap document,
        int documentTop,
        int frameHeight,
        int fixedBottomRows)
    {
        var scrollableHeight = frameHeight - fixedBottomRows;
        var frame = new Bitmap(
            document.Width,
            frameHeight,
            PixelFormat.Format32bppPArgb);
        using var graphics = Graphics.FromImage(frame);
        graphics.CompositingMode = CompositingMode.SourceCopy;
        graphics.DrawImage(
            document,
            new Rectangle(0, 0, document.Width, scrollableHeight),
            new Rectangle(
                0,
                documentTop,
                document.Width,
                scrollableHeight),
            GraphicsUnit.Pixel);
        using var background = new SolidBrush(Color.FromArgb(255, 31, 31, 31));
        using var track = new SolidBrush(Color.FromArgb(255, 55, 55, 55));
        using var thumb = new SolidBrush(Color.FromArgb(255, 118, 118, 118));
        graphics.FillRectangle(
            background,
            0,
            scrollableHeight,
            document.Width,
            fixedBottomRows);
        graphics.FillRectangle(
            track,
            0,
            scrollableHeight,
            document.Width,
            1);
        graphics.FillRectangle(
            thumb,
            document.Width / 4,
            scrollableHeight + 5,
            document.Width / 3,
            8);
        return frame;
    }

    private static Bitmap Crop(Bitmap source, int top, int height)
    {
        return source.Clone(
            new Rectangle(0, top, source.Width, height),
            PixelFormat.Format32bppPArgb);
    }

    private static Color ExpectedBrowserColor(int x, int y)
    {
        var hash = unchecked((uint)((x * 73856093) ^ (y * 19349663)));
        hash ^= hash >> 13;
        hash *= 1274126177;
        return Color.FromArgb(
            255,
            (byte)hash,
            (byte)(hash >> 8),
            (byte)(hash >> 16));
    }

    private static Color ExpectedColor(int x, int y)
    {
        return Color.FromArgb(
            255,
            (x * 19 + y * 31) & 0xff,
            (x * 43 + y * 17) & 0xff,
            (x * 7 + y * 29) & 0xff);
    }
}
