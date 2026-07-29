using System.Diagnostics;
using System.Reflection;
using SnapCut.Core;
using static SnapCut.Core.Tests.TestImages;

namespace SnapCut.Core.Tests;

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
        var previousFrame = CreateFrame(startY: 0, width: 96, height: 100);
        var currentFrame = CreateFrame(startY: 30, width: 96, height: 100);

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
        var firstFrame = CreateFrame(startY: 0, width: 96, height: 100);
        var secondFrame = CreateFrame(startY: 30, width: 96, height: 100);
        using var composer = new ScrollCaptureComposer();

        Assert.True(composer.TryAddFrame(firstFrame, Options, out var firstMatch));
        Assert.Null(firstMatch);
        Assert.True(composer.TryAddFrame(secondFrame, Options, out var secondMatch));
        Assert.NotNull(secondMatch);
        Assert.Equal(70, secondMatch.OverlapRows);

        var result = composer.Compose();

        Assert.Equal(96, result.Width);
        Assert.Equal(130, result.Height);

        for (var y = 0; y < result.Height; y += 7)
        {
            Assert.Equal(ExpectedColor(15, y), GetPixel(result, 15, y));
        }
    }

    [Fact]
    public void RejectsFramesWithoutReliableOverlap()
    {
        var firstFrame = CreateFrame(startY: 0, width: 96, height: 100);
        var unrelatedFrame = CreateSolidFrame(Rgb(19, 33, 57), 96, 100);
        using var composer = new ScrollCaptureComposer();

        Assert.True(composer.TryAddFrame(firstFrame, Options, out _));
        Assert.False(composer.TryAddFrame(unrelatedFrame, Options, out var overlapMatch));
        Assert.Null(overlapMatch);
        Assert.Equal(1, composer.FrameCount);
    }

    [Fact]
    public void RejectsAnUnchangedFrameAtTheEndOfScrollableContent()
    {
        var frame = CreateFrame(startY: 0, width: 96, height: 100);
        using var composer = new ScrollCaptureComposer();

        Assert.True(composer.TryAddFrame(frame, Options, out _));
        Assert.False(composer.TryAddFrame(frame, Options, out var overlapMatch));
        Assert.Null(overlapMatch);
        Assert.Equal(1, composer.FrameCount);
    }

    [Fact]
    public void RejectsRepeatedViewportFramesAtBothScrollableBoundaries()
    {
        const int width = 120;
        const int height = 100;
        var initialFrame = CreateRepeatingFrame(
            width,
            height,
            scrollbarColor: Rgb(255, 0, 0));
        var bottomBoundaryFrame = CreateRepeatingFrame(
            width,
            height,
            scrollbarColor: Rgb(0, 0, 255));
        var topBoundaryFrame = CreateRepeatingFrame(
            width,
            height,
            scrollbarColor: Rgb(0, 128, 0));
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
        var initialFrame = CreateRepeatingFrame(
            width,
            height,
            scrollbarColor: Rgb(255, 0, 0));
        var animatedBoundaryFrame = CreateRepeatingFrame(
            width,
            height,
            scrollbarColor: Rgb(0, 0, 255));
        FillRect(animatedBoundaryFrame, 20, 45, 10, 10, Rgb(255, 0, 255));
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
        var initialFrame = CreateCodeEditorContent(width, height);
        var highlightedFrame = initialFrame.Clone();
        FillRect(highlightedFrame, 48, 126, width - 60, 18, Rgb(42, 45, 51));

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
        var initialFrame = CreateCodeEditorContent(width, height + 2);
        var firstViewport = Crop(initialFrame, 0, height);
        var microMovedViewport = Crop(initialFrame, 2, height);
        FillRect(microMovedViewport, 48, 124, width - 60, 18, Rgb(42, 45, 51));

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
        var previousFrame = CreateBrowserLikeFrame(
            startY: 0,
            width,
            height,
            headerRows,
            scrollbarColor: Rgb(255, 0, 0));
        var currentFrame = CreateBrowserLikeFrame(
            startY: scrollDistance,
            width,
            height,
            headerRows,
            scrollbarColor: Rgb(0, 0, 255));

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
        var previousFrame = CreateBrowserLikeFrame(
            startY: 0,
            width,
            height,
            headerRows,
            scrollbarColor: Rgb(169, 169, 169));
        var currentFrame = CreateBrowserLikeFrame(
            startY: scrollDistance,
            width,
            height,
            headerRows,
            scrollbarColor: Rgb(211, 211, 211));
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
        var content = CreateSparseChatContent(
            width,
            height + (scrollDistance * 2));
        var firstFrame = Crop(content, 0, height);
        var secondFrame = Crop(content, scrollDistance, height);
        var thirdFrame = Crop(content, scrollDistance * 2, height);
        FillRect(secondFrame, 20, 70, 56, 20, Rgb(206, 104, 80));
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

        var result = composer.Compose();

        Assert.Equal(height + (scrollDistance * 2), result.Height);

        for (var y = 0; y < result.Height; y += 5)
        {
            for (var x = 0; x < result.Width; x += 11)
            {
                Assert.Equal(GetPixel(content, x, y), GetPixel(result, x, y));
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
        var content = CreateCodeEditorContent(
            width,
            height + (scrollDistance * 2));
        var firstFrame = Crop(content, 0, height);
        var secondFrame = Crop(content, scrollDistance, height);
        var thirdFrame = Crop(content, scrollDistance * 2, height);
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

        var result = composer.Compose();

        Assert.Equal(height + (scrollDistance * 2), result.Height);

        for (var y = 0; y < result.Height; y += 3)
        {
            for (var x = 0; x < result.Width; x += 7)
            {
                Assert.Equal(GetPixel(content, x, y), GetPixel(result, x, y));
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
            var frame = CreateFrame(startY, width: 96, height: 100);
            Assert.True(composer.TryAddFrame(frame, previewOptions, out _));
        }

        var composite = composer.Compose();
        var preview = composer.ComposePreview(48, 80);
        // The preview must be a single scaling pass over the completed
        // composite — identical to downscaling the composed image once, with
        // no per-segment scaling seams.
        var expected = composite.DownscaleTo(preview.Width, preview.Height);

        for (var y = 0; y < preview.Height; y++)
        {
            for (var x = 0; x < preview.Width; x++)
            {
                Assert.Equal(GetPixel(expected, x, y), GetPixel(preview, x, y));
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
            var frame = CreateFrame(top, width: 96, height: 100);
            Assert.True(composer.TryAddFrame(frame, previewOptions, out _));
        }

        // Within the height budget the preview is the whole image, unscaled.
        {
            var preview = composer.ComposeLivePreview(96, 300);
            Assert.Equal(96, preview.Width);
            Assert.Equal(260, preview.Height);
            for (var y = 0; y < preview.Height; y += 7)
            {
                Assert.Equal(ExpectedColor(15, y), GetPixel(preview, 15, y));
            }
        }

        // Beyond the budget the whole image is scaled down, aspect preserved,
        // never cropped to a slice.
        {
            var preview = composer.ComposeLivePreview(96, 80);
            Assert.Equal(80, preview.Height);
            Assert.Equal(
                (int)Math.Round(96 * (80 / 260d)),
                preview.Width);
        }
    }

    [Fact]
    public void PrependsUpwardContentAndAppendsDownwardContent()
    {
        var initialFrame = CreateFrame(startY: 100, width: 96, height: 100);
        var downwardFrame = CreateFrame(startY: 130, width: 96, height: 100);
        var upwardFrame = CreateFrame(startY: 70, width: 96, height: 100);
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

        var result = composer.Compose();
        var preview = composer.ComposePreview(
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
            Assert.Equal(ExpectedColor(15, y + 70), GetPixel(result, 15, y));
        }
    }

    [Fact]
    public void DoesNotDuplicateContentWhenScrollingBackThroughCapturedFrames()
    {
        var initialFrame = CreateFrame(startY: 100, width: 96, height: 100);
        var downwardFrame = CreateFrame(startY: 130, width: 96, height: 100);
        var returnedInitialFrame = CreateFrame(startY: 100, width: 96, height: 100);
        var upwardFrame = CreateFrame(startY: 70, width: 96, height: 100);
        var returnedDownwardFrame = CreateFrame(startY: 130, width: 96, height: 100);
        var fartherDownwardFrame = CreateFrame(startY: 160, width: 96, height: 100);
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

        var result = composer.Compose();

        for (var y = 0; y < result.Height; y += 7)
        {
            Assert.Equal(ExpectedColor(15, y + 70), GetPixel(result, 15, y));
        }
    }

    [Fact]
    public void PrependsNewContentAtTopAfterScrollingDownThenBackUp()
    {
        // Regression: after down-capture and return to the top boundary, further
        // Up with unseen content must prepend. Known-viewport matching must not
        // lock onto the initial frame (FrameTop equality) and block expansion.
        var initialFrame = CreateFrame(startY: 100, width: 96, height: 100);
        var downwardFrame = CreateFrame(startY: 140, width: 96, height: 100);
        var returnedInitialFrame = CreateFrame(startY: 100, width: 96, height: 100);
        var furtherUpFrame = CreateFrame(startY: 50, width: 96, height: 100);
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

        var result = composer.Compose();
        for (var y = 0; y < result.Height; y += 5)
        {
            Assert.Equal(ExpectedColor(15, y + 50), GetPixel(result, 15, y));
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
        var content = CreateWeChatLikeChatContent(width, height + startTop + downStep);
        using var composer = new ScrollCaptureComposer();

        Assert.True(composer.TryAddFrame(Crop(content, startTop, height), options, out _));

        Assert.True(composer.TryAddFrame(
            Crop(content, startTop + downStep, height),
            ScrollCaptureDirection.Down,
            options,
            out _));

        Assert.False(composer.TryAddFrame(
            Crop(content, startTop, height),
            ScrollCaptureDirection.Up,
            options,
            out _));

        Assert.True(
            composer.TryAddFrame(
                Crop(content, furtherUpTop, height),
                ScrollCaptureDirection.Up,
                options,
                out _),
            "Further Up at the top boundary must prepend unseen chat content.");

        Assert.True(composer.AddedAboveFrameCount >= 1);
        Assert.Equal(height + startTop + downStep - furtherUpTop, composer.OutputHeight);

        var result = composer.Compose();
        for (var y = 0; y < result.Height; y += 4)
        {
            for (var x = 0; x < result.Width; x += 11)
            {
                Assert.Equal(GetPixel(content, x, y + furtherUpTop), GetPixel(result, x, y));
            }
        }
    }

    [Fact]
    public void PrependsUnseenContentWhenTheWheelDirectionIsStale()
    {
        var initialFrame = CreateFrame(startY: 100, width: 96, height: 100);
        var downwardFrame = CreateFrame(startY: 150, width: 96, height: 100);
        var returnedInitialFrame = CreateFrame(
            startY: 100,
            width: 96,
            height: 100);
        var upwardFrame = CreateFrame(startY: 50, width: 96, height: 100);
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

        var result = composer.Compose();

        Assert.Equal(200, result.Height);

        for (var y = 0; y < result.Height; y += 7)
        {
            Assert.Equal(ExpectedColor(15, y + 50), GetPixel(result, 15, y));
        }
    }

    [Fact]
    public void RecognizesAReturnedViewportDespiteASmallVisualUpdate()
    {
        var initialFrame = CreateFrame(startY: 100, width: 120, height: 120);
        var downwardFrame = CreateFrame(startY: 160, width: 120, height: 120);
        var returnedInitialFrame = CreateFrame(
            startY: 100,
            width: 120,
            height: 120);
        FillRect(returnedInitialFrame, 30, 48, 12, 12, Rgb(255, 0, 255));
        var upperFrame = CreateFrame(startY: 40, width: 120, height: 120);
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
    public void KeepsEveryRowContiguousAcrossDownUpDownFrameStitching()
    {
        var initialFrame = CreateFrame(startY: 200, width: 96, height: 100);
        var firstDownwardFrame = CreateFrame(
            startY: 240,
            width: 96,
            height: 100);
        var secondDownwardFrame = CreateFrame(
            startY: 280,
            width: 96,
            height: 100);
        var returnedFrame = CreateFrame(startY: 240, width: 96, height: 100);
        var upwardFrame = CreateFrame(startY: 160, width: 96, height: 100);
        var returnedDownwardFrame = CreateFrame(
            startY: 240,
            width: 96,
            height: 100);
        var finalDownwardFrame = CreateFrame(startY: 320, width: 96, height: 100);
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

        var result = composer.Compose();

        Assert.Equal(260, result.Height);

        for (var y = 0; y < result.Height; y++)
        {
            Assert.Equal(ExpectedColor(15, y + 160), GetPixel(result, 15, y));
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
        var content = CreateCodeEditorContent(width, contentHeight);
        using var composer = new ScrollCaptureComposer();

        var lastFrameTop = contentHeight - height;

        // Scroll all the way down, capturing every viewport to the bottom.
        for (var top = 0; top <= lastFrameTop; top += scrollDistance)
        {
            var frame = Crop(content, top, height);
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
            var frame = Crop(content, top, height);
            // A one-pixel-wide caret near the top-left, like a text cursor.
            FillRect(frame, 70, 6, 1, 12, Rgb(220, 220, 220));

            composer.TryAddFrame(frame, ScrollCaptureDirection.Up, codeOptions, out _);
        }

        Assert.Equal(heightAtBottom, composer.OutputHeight);
        Assert.Equal(framesAtBottom, composer.FrameCount);
        Assert.Equal(contentHeight, composer.OutputHeight);

        var result = composer.Compose();

        // Every stitched row must line up with the original document — no
        // duplicated or torn middle rows at the bottom.
        for (var y = 0; y < result.Height; y += 3)
        {
            for (var x = 0; x < result.Width; x += 7)
            {
                Assert.Equal(GetPixel(content, x, y), GetPixel(result, x, y));
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
        var content = CreateCodeEditorContent(width, height + 222);
        using var composer = new ScrollCaptureComposer();

        for (var index = 0; index < tops.Length; index++)
        {
            var frame = Crop(content, tops[index], height);
            composer.TryAddFrame(frame, directions[index], options, out _);
        }

        Assert.Equal(height + 222, composer.OutputHeight);
        var result = composer.Compose();

        for (var y = 0; y < result.Height; y += 3)
        {
            for (var x = 0; x < result.Width; x += 7)
            {
                Assert.Equal(GetPixel(content, x, y), GetPixel(result, x, y));
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
        var content = CreatePeriodicBandContent(width, height + (scrollDistance * 5), period);
        var first = Crop(content, 0, height);
        var second = Crop(content, scrollDistance, height);
        var third = Crop(content, scrollDistance * 2, height);

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
        var content = CreateCodeEditorContent(width, height + tops[^1]);
        using var composer = new ScrollCaptureComposer();

        foreach (var top in tops)
        {
            var frame = Crop(content, top, height);
            composer.TryAddFrame(frame, ScrollCaptureDirection.Down, options, out _);
        }

        Assert.Equal(height + tops[^1], composer.OutputHeight);
        var result = composer.Compose();

        for (var y = 0; y < result.Height; y += 2)
        {
            for (var x = 0; x < result.Width; x += 5)
            {
                Assert.Equal(GetPixel(content, x, y), GetPixel(result, x, y));
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
        var content = CreateCodeEditorContent(
            width,
            height + tops[^1]);
        using var composer = new ScrollCaptureComposer();

        foreach (var top in tops)
        {
            var frame = Crop(content, top, height);
            Assert.True(
                composer.TryAddFrame(
                    frame,
                    ScrollCaptureDirection.Down,
                    options,
                    out _),
                $"failed at top={top}");
        }

        Assert.Equal(height + tops[^1], composer.OutputHeight);

        var result = composer.Compose();
        for (var y = 0; y < result.Height; y += 3)
        {
            for (var x = 0; x < result.Width; x += 9)
            {
                Assert.Equal(GetPixel(content, x, y), GetPixel(result, x, y));
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
        var content = CreateCodeEditorContent(width, height + tops[^1]);
        using var composer = new ScrollCaptureComposer();

        foreach (var top in tops)
        {
            var frame = Crop(content, top, height);
            Assert.True(
                composer.TryAddFrame(frame, ScrollCaptureDirection.Down, options, out _),
                $"failed at top={top}");
        }

        Assert.Equal(height + tops[^1], composer.OutputHeight);
        var result = composer.Compose();
        for (var y = 0; y < result.Height; y += 4)
        {
            Assert.Equal(GetPixel(content, 40, y), GetPixel(result, 40, y));
        }
    }

    [Fact]
    public void AppliesHorizontalMicroAlignmentWhenComposingShiftedFrames()
    {
        // WeChat-like capture can drift by 1px horizontally across frames
        // (window chrome / compositor). Matching already recovers
        // HorizontalOffset; compose must apply it or the stitch shows a jagged
        // vertical seam.
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

        var content = CreateGradientContent(width + shift, height + scroll);
        var firstFrame = new PixelImage(width, height);
        var secondFrame = new PixelImage(width, height);

        // first frame: content columns [0, width)
        // second frame: content columns [shift, width+shift) at y+scroll — a 1px
        // horizontal compositor drift relative to the first frame.
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                SetPixel(firstFrame, x, y, GetPixel(content, x, y));
                SetPixel(secondFrame, x, y, GetPixel(content, x + shift, y + scroll));
            }
        }

        using var composer = new ScrollCaptureComposer();
        Assert.True(composer.TryAddFrame(firstFrame, options, out _));
        Assert.True(composer.TryAddFrame(secondFrame, options, out var match));
        Assert.NotNull(match);
        Assert.Equal(height - scroll, match!.OverlapRows);
        Assert.Equal(-shift, match.HorizontalOffset);
        Assert.Equal(height + scroll, composer.OutputHeight);

        var result = composer.Compose();
        // Top region comes from the unshifted first frame.
        for (var y = 0; y < height - 4; y += 3)
        {
            for (var x = 0; x < width - shift; x += 5)
            {
                Assert.Equal(GetPixel(content, x, y), GetPixel(result, x, y));
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
                Assert.Equal(GetPixel(content, x, y), GetPixel(result, x, y));
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
        var content = CreateWeChatLikeChatContent(width, height + tops[^1]);
        using var composer = new ScrollCaptureComposer();

        foreach (var top in tops)
        {
            var frame = Crop(content, top, height);
            composer.TryAddFrame(frame, ScrollCaptureDirection.Down, options, out _);
        }

        Assert.Equal(height + tops[^1], composer.OutputHeight);
        var result = composer.Compose();

        for (var y = 0; y < result.Height; y += 3)
        {
            for (var x = 0; x < result.Width; x += 9)
            {
                Assert.Equal(GetPixel(content, x, y), GetPixel(result, x, y));
            }
        }
    }

    [Fact]
    public void TemporalPriorResolvesContinuousScrollsWithinTightBudget()
    {
        const int width = 360;
        const int height = 280;
        const int scrollDistance = 36;
        var content = CreateGradientContent(width, height + (scrollDistance * 4));
        var previousFrame = Crop(content, scrollDistance, height);
        var currentFrame = Crop(content, scrollDistance * 2, height);

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
        var content = CreateGradientContent(width, height + smallStep + largeStep + 40);
        var previousFrame = Crop(content, smallStep, height);
        var currentFrame = Crop(content, smallStep + largeStep, height);

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

    private static PixelImage CreateWeChatLikeChatContent(int width, int height)
    {
        var image = new PixelImage(width, height);
        Fill(image, Rgb(237, 237, 237));

        // Fixed-ish top title bar texture (partial sticky feel when cropped mid-scroll).
        FillRect(image, 0, 0, width, 36, Rgb(246, 246, 246));

        for (var message = 0; message * 56 < height; message++)
        {
            var top = 40 + (message * 56);
            var isSelf = message % 3 == 0;
            var avatarX = isSelf ? width - 44 : 12;
            var bubbleX = isSelf ? width - 200 : 56;
            var avatarColor = Rgb(
                (byte)(40 + ((message * 37) % 160)),
                (byte)(80 + ((message * 53) % 120)),
                (byte)(100 + ((message * 19) % 100)));
            var bubbleColor = isSelf
                ? Rgb(149, 236, 105)
                : Rgb(255, 255, 255);

            FillRect(image, avatarX, top, 32, 32, avatarColor);
            FillRect(image, bubbleX, top + 2, 140, 36, bubbleColor);

            // Repeated short text strokes inside bubbles — the failure mode WeChat
            // stitchers must not latch onto when two messages look similar.
            for (var line = 0; line < 3; line++)
            {
                var y = top + 8 + (line * 8);
                FillRect(
                    image,
                    bubbleX + 10,
                    y,
                    100 - (line * 12) + 1,
                    2,
                    Rgb(60, 60, 60));
            }
        }

        return image;
    }

    private static PixelImage CreatePeriodicBandContent(int width, int height, int period)
    {
        var image = new PixelImage(width, height);
        Fill(image, Rgb(245, 245, 245));

        for (var y = 0; y < height; y++)
        {
            var band = (y / period) % 4;
            var color = band switch
            {
                0 => Rgb(48, 52, 64),
                1 => Rgb(86, 120, 180),
                2 => Rgb(210, 176, 96),
                _ => Rgb(132, 92, 148),
            };
            FillRect(image, 0, y, width - 12, 1, color);
        }

        // Stable left gutter so sparse-anchor logic has texture without breaking
        // the periodic body.
        FillRect(image, 0, 0, 28, height, Rgb(28, 30, 36));

        for (var y = 0; y < height; y += 12)
        {
            FillRect(image, 6, y + 3, 14, 5, Rgb(180, 180, 185));
        }

        return image;
    }

    private static PixelImage CreateGradientContent(int width, int height)
    {
        var content = new PixelImage(width, height);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                SetPixel(content, x, y, ExpectedColor(x, y));
            }
        }

        return content;
    }

    private static PixelImage CreateFrame(int startY, int width, int height)
    {
        var frame = new PixelImage(width, height);

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                SetPixel(frame, x, y, ExpectedColor(x, startY + y));
            }
        }

        return frame;
    }

    private static PixelImage CreateSolidFrame(uint color, int width, int height)
    {
        var frame = new PixelImage(width, height);
        Fill(frame, color);
        return frame;
    }

    private static PixelImage CreateRepeatingFrame(
        int width,
        int height,
        uint scrollbarColor)
    {
        const int period = 20;
        var frame = new PixelImage(width, height);

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                SetPixel(frame, x, y, ExpectedColor(x, y % period));
            }
        }

        FillRect(frame, width - 10, 0, 10, height, scrollbarColor);
        return frame;
    }

    private static PixelImage CreateBrowserLikeFrame(
        int startY,
        int width,
        int height,
        int headerRows,
        uint scrollbarColor)
    {
        var frame = new PixelImage(width, height);

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var color = y < headerRows
                    ? Rgb(
                        (byte)((x * 11 + y * 3) & 0xff),
                        (byte)((x * 5 + 41) & 0xff),
                        (byte)((y * 17 + 73) & 0xff))
                    : ExpectedBrowserColor(x, startY + y - headerRows);

                if (x >= width - 10)
                {
                    color = scrollbarColor;
                }

                SetPixel(frame, x, y, color);
            }
        }

        return frame;
    }

    private static PixelImage CreateSparseChatContent(int width, int height)
    {
        var content = new PixelImage(width, height);
        Fill(content, Rgb(31, 31, 31));
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
                ? Rgb(46, 204, 142)
                : Rgb(60, 60, 62);
            var markerColor = Rgb(
                (byte)((index * 53 + 91) & 0xff),
                (byte)((index * 89 + 47) & 0xff),
                (byte)((index * 29 + 151) & 0xff));

            FillRect(content, avatarLeft, messageTop, 20, 20, Rgb(175, 175, 175));
            FillRect(content, avatarLeft + 4, messageTop + 4, 12, 12, markerColor);
            FillRect(
                content,
                bubbleLeft,
                messageTop,
                bubbleWidth,
                bubbleHeight,
                bubbleColor);

            for (var line = 0; line < 3; line++)
            {
                var lineWidth = bubbleWidth - 20 - ((line * 11 + index * 7) % 22);
                FillRect(
                    content,
                    bubbleLeft + 10,
                    messageTop + 8 + (line * 8),
                    lineWidth,
                    3,
                    Rgb(226, 226, 226));
            }
        }

        return content;
    }

    private static PixelImage CreateCodeEditorContent(int width, int height)
    {
        var content = new PixelImage(width, height);
        Fill(content, Rgb(30, 34, 39));
        var gutterColor = Rgb(35, 40, 46);
        var codeColor = Rgb(104, 170, 212);
        var punctuationColor = Rgb(213, 149, 98);
        var numberColor = Rgb(82, 99, 116);
        const int lineHeight = 18;
        const int gutterWidth = 54;

        for (var line = 0; line * lineHeight < height; line++)
        {
            var top = line * lineHeight;
            FillRect(content, 0, top, gutterWidth, lineHeight - 1, gutterColor);

            // Encode the line number as stable bars in the gutter. The body is
            // intentionally repetitive so the gutter is the useful anchor.
            for (var bit = 0; bit < 10; bit++)
            {
                if (((line + 1) & (1 << bit)) == 0)
                {
                    continue;
                }

                FillRect(
                    content,
                    5 + (bit * 4),
                    top + 4,
                    3,
                    10,
                    numberColor);
            }

            for (var token = 0; token < 7; token++)
            {
                var tokenLeft = gutterWidth + 16 + (token * 31);
                FillRect(
                    content,
                    tokenLeft,
                    top + 5,
                    20 + ((token % 2) * 8),
                    3,
                    token % 3 == 0 ? punctuationColor : codeColor);
            }
        }

        return content;
    }

    private static uint ExpectedBrowserColor(int x, int y)
    {
        var hash = unchecked((uint)((x * 73856093) ^ (y * 19349663)));
        hash ^= hash >> 13;
        hash *= 1274126177;
        return Rgb(
            (byte)hash,
            (byte)(hash >> 8),
            (byte)(hash >> 16));
    }

    private static uint ExpectedColor(int x, int y)
    {
        return Rgb(
            (byte)((x * 19 + y * 31) & 0xff),
            (byte)((x * 43 + y * 17) & 0xff),
            (byte)((x * 7 + y * 29) & 0xff));
    }
}
