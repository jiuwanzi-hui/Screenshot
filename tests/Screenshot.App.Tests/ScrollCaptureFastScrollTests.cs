using System.Drawing;
using System.Drawing.Imaging;
using System.Diagnostics;
using Screenshot.App.Capture;

namespace Screenshot.App.Tests;

/// <summary>
/// Regression coverage for stitching while the user scrolls fast.
/// </summary>
/// <remarks>
/// A fling moves the viewport by hundreds of rows between two samples the
/// matcher can actually resolve, so these tests use displacements that are a
/// large fraction of the viewport instead of the small continuous steps the
/// other suites cover, and they feed deliberately poor wheel estimates.
/// </remarks>
public sealed class ScrollCaptureFastScrollTests
{
    private static readonly ScrollCaptureOptions FastOptions = new(
        MaximumFrames: 40,
        ScrollDelta: -240,
        MinimumOverlapRows: 20,
        MinimumOverlapConfidence: 0.945,
        MinimumNewRows: 4,
        FrameDelayMilliseconds: 1);

    [Theory]
    [InlineData(0.35)]
    [InlineData(0.55)]
    [InlineData(0.70)]
    [InlineData(0.85)]
    public void MatchesLargeSingleFrameDisplacements(double displacementRatio)
    {
        const int width = 520;
        const int height = 640;
        var newRows = (int)Math.Round(height * displacementRatio);
        using var document = CreateDocumentContent(width, height + newRows + 32);
        using var previousFrame = Crop(document, 0, height);
        using var currentFrame = Crop(document, newRows, height);

        var match = ImageOverlapMatcher.FindVerticalOverlap(
            previousFrame,
            currentFrame,
            minimumOverlapRows: Math.Max(20, Math.Min(96, height / 12)),
            minimumConfidence: 0.945,
            minimumNewRows: 4);

        Assert.NotNull(match);
        Assert.Equal(height - newRows, match.OverlapRows);
    }

    [Fact]
    public void StitchesAnAcceleratingFlingWithoutGapsOrDuplicates()
    {
        // Steps grow the way a real wheel fling does and then decay again. The
        // stitch has to follow every one of them; a frozen step size shows up
        // as either a gap or repeated content in the composite.
        const int width = 480;
        const int height = 560;
        var steps = new[] { 18, 46, 110, 240, 380, 470, 300, 140, 52, 16 };
        var tops = new int[steps.Length + 1];
        for (var index = 0; index < steps.Length; index++)
        {
            tops[index + 1] = tops[index] + steps[index];
        }

        using var document = CreateDocumentContent(width, height + tops[^1]);
        using var composer = new ScrollCaptureComposer();

        foreach (var top in tops)
        {
            using var frame = Crop(document, top, height);
            Assert.True(
                composer.TryAddFrame(
                    frame,
                    ScrollCaptureDirection.Down,
                    FastOptions,
                    out _),
                $"未能拼接 top={top} 的帧。");
        }

        Assert.Equal(height + tops[^1], composer.OutputHeight);
        AssertMatchesDocument(document, composer);
    }

    [Fact]
    public void KeepsFollowingTheViewportWhenTheWheelEstimateIsFarTooSmall()
    {
        // The wheel calibration lags acceleration and any sample the pipeline
        // had to skip moved the viewport without contributing to the estimate,
        // so the estimate is routinely a fraction of the real displacement.
        // A decisive image match must not be discarded because of it.
        const int width = 480;
        const int height = 560;
        const int newRows = 384;
        using var document = CreateDocumentContent(width, height + newRows + 32);
        using var composer = new ScrollCaptureComposer();
        using var firstFrame = Crop(document, 0, height);
        using var secondFrame = Crop(document, newRows, height);

        Assert.True(composer.TryAddFrame(firstFrame, FastOptions, out _));
        Assert.True(
            composer.TryAddFrame(
                secondFrame,
                ScrollCaptureDirection.Down,
                FastOptions,
                expectedNewRows: newRows / 4,
                lockDirection: true,
                out var overlapMatch),
            "滚轮估计偏小时丢弃了明确的图像匹配。");

        Assert.NotNull(overlapMatch);
        Assert.Equal(height - newRows, overlapMatch.OverlapRows);
        Assert.Equal(height + newRows, composer.OutputHeight);
    }

    [Fact]
    public void StitchesMixedSpeedScrollingWithCreepsPausesAndBursts()
    {
        // The second user-reported failure: slow scrolling, a pause, then
        // scrolling again produced seams. The chain has to survive steps below
        // the minimum displacement (dropped, then covered by an accumulated
        // step), exact standstills, and sudden bursts — all without wheel
        // evidence, the way smooth-scroll inertia delivers them.
        const int width = 480;
        const int height = 560;
        // The final step is above the minimum displacement so the tail creep
        // is carried into a stitchable step, the way a real capture ends with
        // the completion settle frame.
        var steps = new[]
        {
            2, 2, 3, 0, 6, 12, 0, 0, 180, 320, 24, 2, 0, 2, 90, 260, 8, 0, 3, 6,
        };
        using var document = CreateDocumentContent(
            width,
            height + steps.Sum());
        using var composer = new ScrollCaptureComposer();

        using (var firstFrame = Crop(document, 0, height))
        {
            Assert.True(composer.TryAddFrame(firstFrame, FastOptions, out _));
        }

        var top = 0;
        var pendingRows = 0;
        foreach (var step in steps)
        {
            top += step;
            pendingRows += step;
            using var frame = Crop(document, top, height);
            var added = composer.TryAddFrame(
                frame,
                ScrollCaptureDirection.Down,
                FastOptions,
                expectedNewRows: null,
                lockDirection: false,
                out _);

            if (pendingRows >= FastOptions.MinimumNewRows)
            {
                Assert.True(
                    added || composer.LastFrameMovementRows is not null,
                    $"混合速度链在 top={top} 丢失锚点。");
            }

            if (composer.LastFrameMovementRows is not null)
            {
                pendingRows = 0;
            }
        }

        Assert.Equal(height + steps.Sum(), composer.OutputHeight);
        AssertMatchesDocument(document, composer);
    }

    [Fact]
    public void FastReverseScrollExtendsTheCaptureAboveTheStartingViewport()
    {
        // The user-reported failure: capture starts mid-document, a fling
        // scrolls down, the page rests, then an accelerating fling scrolls
        // back up past the starting viewport. The up-run first travels
        // through already captured content — the anchor has to follow it
        // frame by frame, without any wheel evidence (smooth-scroll inertia
        // emits none), so the strips above the original top still connect.
        const int width = 480;
        const int height = 560;
        const int startTop = 1400;
        var downSteps = new[] { 24, 70, 180, 320, 260, 90, 20 };
        var upSteps = new[] { 30, 90, 220, 380, 430, 360, 200, 60, 18 };
        var bottomTop = startTop + downSteps.Sum();
        var finalTop = bottomTop - upSteps.Sum();
        using var document = CreateDocumentContent(
            width,
            bottomTop + height + 32);
        using var composer = new ScrollCaptureComposer();

        using (var firstFrame = Crop(document, startTop, height))
        {
            Assert.True(composer.TryAddFrame(firstFrame, FastOptions, out _));
        }

        var top = startTop;
        foreach (var step in downSteps)
        {
            top += step;
            using var frame = Crop(document, top, height);
            Assert.True(
                composer.TryAddFrame(
                    frame,
                    ScrollCaptureDirection.Down,
                    FastOptions,
                    expectedNewRows: null,
                    lockDirection: false,
                    out _),
                $"下行链在 top={top} 断裂。");
        }

        foreach (var step in upSteps)
        {
            top -= step;
            using var frame = Crop(document, top, height);
            var added = composer.TryAddFrame(
                frame,
                ScrollCaptureDirection.Up,
                FastOptions,
                expectedNewRows: null,
                lockDirection: false,
                out _);
            Assert.True(
                added || composer.LastFrameMovementRows is not null,
                $"上行链在 top={top} 丢失锚点。");
        }

        Assert.Equal(finalTop, top);
        Assert.True(
            composer.AddedAboveFrameCount > 0,
            "反向滚动没有在顶部扩展任何内容。");
        Assert.Equal(bottomTop + height - finalTop, composer.OutputHeight);
        AssertMatchesDocument(document, composer, finalTop);
    }

    [Theory]
    [InlineData(320, 240)]
    [InlineData(760, 520)]
    [InlineData(1280, 720)]
    public void RemainsResponsiveAcrossMixedWheelBurstsAndRepeatedReversals(
        int width,
        int height)
    {
        // Slow movement, a downward fling, a sustained reverse run, another
        // downward fling, and a final pair of short reversals. Run the same
        // hostile sequence at three common selection sizes so matcher latency
        // cannot hide behind one fixture.
        var moves = new[]
        {
            4,
            12,
            Math.Max(24, height / 7),
            Math.Max(40, height / 3),
            Math.Max(60, (height * 3) / 5),
            Math.Max(28, height / 5),
            -Math.Max(20, height / 9),
            -Math.Max(48, height / 3),
            -Math.Max(72, (height * 2) / 3),
            -Math.Max(36, height / 4),
            Math.Max(16, height / 10),
            Math.Max(52, height / 2),
            Math.Max(72, (height * 3) / 4),
            -Math.Max(28, height / 5),
            Math.Max(32, height / 4),
        };
        var startTop = height * 4;
        var positions = new List<int> { startTop };
        foreach (var move in moves)
        {
            positions.Add(positions[^1] + move);
        }

        var minimumTop = positions.Min();
        var maximumTop = positions.Max();
        Assert.True(minimumTop >= 0);
        using var document = CreateDocumentContent(
            width,
            maximumTop + height + 32);
        using var composer = new ScrollCaptureComposer();
        var maximumFrameDuration = TimeSpan.Zero;
        var options = FastOptions with { MaximumFrames = positions.Count + 4 };

        for (var index = 0; index < positions.Count; index++)
        {
            using var frame = Crop(document, positions[index], height);
            var direction = index == 0 || moves[index - 1] >= 0
                ? ScrollCaptureDirection.Down
                : ScrollCaptureDirection.Up;
            var stopwatch = Stopwatch.StartNew();
            var added = composer.TryAddFrame(
                frame,
                direction,
                options,
                expectedNewRows: index == 0 ? null : Math.Abs(moves[index - 1]),
                lockDirection: index > 0,
                out _);
            stopwatch.Stop();
            maximumFrameDuration = stopwatch.Elapsed > maximumFrameDuration
                ? stopwatch.Elapsed
                : maximumFrameDuration;

            Assert.True(
                added || composer.LastFrameMovementRows is not null,
                $"选区 {width}x{height} 在第 {index} 帧丢失锚点，" +
                $"top={positions[index]}，原因={composer.LastRejectReason}。");
        }

        Assert.True(composer.AddedAboveFrameCount > 0);
        Assert.True(composer.AddedBelowFrameCount > 0);
        Assert.Equal(maximumTop - minimumTop + height, composer.OutputHeight);
        Assert.True(
            maximumFrameDuration < TimeSpan.FromSeconds(1.5),
            $"单帧最长处理 {maximumFrameDuration.TotalMilliseconds:F0}ms，" +
            "会使长截图预览看起来卡住。");
        AssertMatchesDocument(document, composer, minimumTop);
    }

    [Theory]
    [InlineData(320, 240, 73)]
    [InlineData(760, 420, 137)]
    [InlineData(1280, 560, 211)]
    public void KeepsCompleteDocumentAcrossRepeatedBoundaryAndJitterCycles(
        int width,
        int height,
        int maximumStep)
    {
        var documentHeight = (height * 5) + 137;
        var bottomTop = documentHeight - height;
        var startTop = height * 2;
        var options = FastOptions with { MaximumFrames = 240 };
        using var document = CreateDocumentContent(width, documentHeight);
        using var composer = new ScrollCaptureComposer();
        var currentTop = startTop;
        using (var initial = Crop(document, currentTop, height))
        {
            Assert.True(composer.TryAddFrame(initial, options, out _));
        }

        void FeedPosition(int nextTop)
        {
            var direction = nextTop >= currentTop
                ? ScrollCaptureDirection.Down
                : ScrollCaptureDirection.Up;
            var expectedRows = Math.Max(8, Math.Abs(nextTop - currentTop));
            using var frame = Crop(document, nextTop, height);
            var stopwatch = Stopwatch.StartNew();
            composer.TryAddFrame(
                frame,
                direction,
                options,
                expectedRows,
                lockDirection: true,
                out _);
            stopwatch.Stop();
            Assert.True(
                composer.LastFrameMovementRows is not null,
                $"top={currentTop}->{nextTop} 丢失锚点，原因={composer.LastRejectReason}。");
            Assert.True(
                stopwatch.Elapsed < TimeSpan.FromSeconds(1.5),
                $"top={currentTop}->{nextTop} 处理耗时 {stopwatch.Elapsed.TotalMilliseconds:F0}ms。");
            currentTop = nextTop;
        }

        void FeedLeg(int targetTop, int seed)
        {
            var direction = Math.Sign(targetTop - currentTop);
            var stepIndex = 0;
            while (currentTop != targetTop)
            {
                var variedStep = (stepIndex % 5) switch
                {
                    0 => Math.Max(8, maximumStep / 5),
                    1 => maximumStep,
                    2 => Math.Max(12, maximumStep / 2),
                    3 => Math.Max(8, maximumStep / 3),
                    _ => Math.Max(10, (maximumStep * 4) / 5),
                };
                variedStep += (seed + stepIndex * 7) % 11;
                var remaining = Math.Abs(targetTop - currentTop);
                FeedPosition(currentTop + (direction * Math.Min(variedStep, remaining)));
                stepIndex++;
            }
        }

        // Start in the middle, reach the bottom, keep scrolling against the
        // physical edge, then reverse through the whole captured range.
        FeedLeg(bottomTop, seed: 1);
        using (var bottom = Crop(document, bottomTop, height))
        {
            for (var index = 0; index < 4; index++)
            {
                Assert.False(composer.TryAddFrame(
                    bottom,
                    ScrollCaptureDirection.Down,
                    options,
                    expectedNewRows: maximumStep,
                    lockDirection: true,
                    out _));
            }

            Assert.True(composer.TryMarkBoundaryReached(
                bottom,
                ScrollCaptureDirection.Down));
        }

        FeedLeg(0, seed: 2);
        using (var top = Crop(document, 0, height))
        {
            for (var index = 0; index < 4; index++)
            {
                Assert.False(composer.TryAddFrame(
                    top,
                    ScrollCaptureDirection.Up,
                    options,
                    expectedNewRows: maximumStep,
                    lockDirection: true,
                    out _));
            }

            Assert.True(composer.TryMarkBoundaryReached(
                top,
                ScrollCaptureDirection.Up));
        }

        Assert.Equal(documentHeight, composer.OutputHeight);
        AssertMatchesDocument(document, composer);

        // Two full cycles with direction jitter near both edges. Once both
        // physical boundaries are confirmed, none of these return paths may
        // change the output height or duplicate an edge strip.
        for (var cycle = 0; cycle < 2; cycle++)
        {
            FeedLeg(bottomTop, seed: 10 + cycle);
            FeedPosition(bottomTop - 23);
            FeedPosition(bottomTop);
            FeedPosition(bottomTop - 9);
            FeedPosition(bottomTop);
            FeedLeg(0, seed: 20 + cycle);
            FeedPosition(17);
            FeedPosition(0);
            FeedPosition(8);
            FeedPosition(0);

            Assert.Equal(documentHeight, composer.OutputHeight);
            AssertMatchesDocument(document, composer);
        }
    }

    [Fact]
    public void DecimationDropsAMiddleSampleRatherThanAnEnd()
    {
        var index = ScrollFrameSelection.SelectDecimationIndex(9);

        Assert.InRange(index, 1, 7);
    }

    [Fact]
    public void MergingTwoSamplesAddsTheirWheelMotion()
    {
        var tracker = new ScrollWheelMotionTracker();
        var options = ScrollCaptureOptions.Default;
        var earlier = new ScrollWheelMotionSample(
            ScrollCaptureDirection.Down,
            ExpectedRows: 120,
            Delta: -120);
        var later = new ScrollWheelMotionSample(
            ScrollCaptureDirection.Down,
            ExpectedRows: 240,
            Delta: -240);

        var merged = tracker.MergeMotion(earlier, later, 720, options);

        Assert.Equal(-360, merged.Delta);
        Assert.Equal(ScrollCaptureDirection.Down, merged.Direction);
        Assert.Equal(
            tracker.GetExpectedRowsForDelta(720, options, -360),
            merged.ExpectedRows);
    }

    [Fact]
    public void MergingKeepsOnlyTheLatestRunAcrossAReversal()
    {
        var tracker = new ScrollWheelMotionTracker();
        var options = ScrollCaptureOptions.Default;
        var earlier = new ScrollWheelMotionSample(
            ScrollCaptureDirection.Down,
            ExpectedRows: 120,
            Delta: -120);
        var later = new ScrollWheelMotionSample(
            ScrollCaptureDirection.Up,
            ExpectedRows: 60,
            Delta: 60);

        var merged = tracker.MergeMotion(earlier, later, 720, options);

        Assert.Equal(60, merged.Delta);
        Assert.Equal(ScrollCaptureDirection.Up, merged.Direction);
    }

    [Fact]
    public void MergingIgnoresSamplesWithoutWheelInput()
    {
        var tracker = new ScrollWheelMotionTracker();
        var options = ScrollCaptureOptions.Default;
        var withInput = new ScrollWheelMotionSample(
            ScrollCaptureDirection.Down,
            ExpectedRows: 120,
            Delta: -120);
        var withoutInput = new ScrollWheelMotionSample(
            ScrollCaptureDirection.Down,
            ExpectedRows: null,
            Delta: 0);

        Assert.Equal(
            withInput.Delta,
            tracker.MergeMotion(withInput, withoutInput, 720, options).Delta);
        Assert.Equal(
            withInput.Delta,
            tracker.MergeMotion(withoutInput, withInput, 720, options).Delta);
    }

    [Fact]
    public void ExpectedRowsForAnExplicitDeltaUsesTheLearnedCalibration()
    {
        var tracker = new ScrollWheelMotionTracker();
        var options = ScrollCaptureOptions.Default;

        tracker.ObserveMovement(rows: 180, sourceDelta: -240);

        Assert.Equal(360, tracker.GetExpectedRowsForDelta(900, options, -480));
    }

    private static void AssertMatchesDocument(
        Bitmap document,
        ScrollCaptureComposer composer,
        int documentTop = 0)
    {
        using var result = composer.Compose();
        if (documentTop == 0)
        {
            Assert.Equal(document.Height, result.Height);
        }

        for (var y = 0; y < result.Height; y += 3)
        {
            for (var x = 0; x < result.Width; x += 7)
            {
                Assert.Equal(
                    document.GetPixel(x, y + documentTop).ToArgb(),
                    result.GetPixel(x, y).ToArgb());
            }
        }
    }

    /// <summary>
    /// A dense, non-repeating page: text-like glyph rows over a light
    /// background with periodic section bands.
    /// </summary>
    /// <remarks>
    /// The sparse fixtures used elsewhere only put a few hundred lit pixels on
    /// each line, which the matcher's sampling grid can miss on a large
    /// viewport. Real pages a user scrolls through are dense, so the fast
    /// scroll cases are exercised against dense content.
    /// </remarks>
    private static Bitmap CreateDocumentContent(int width, int height)
    {
        var content = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
        using var graphics = Graphics.FromImage(content);
        graphics.Clear(Color.FromArgb(255, 250, 250, 248));
        using var bandBrush = new SolidBrush(Color.FromArgb(255, 232, 236, 244));
        const int lineHeight = 22;

        for (var line = 0; line * lineHeight < height; line++)
        {
            var top = line * lineHeight;

            if (line % 17 == 0)
            {
                graphics.FillRectangle(bandBrush, 0, top + 4, width, 12);
            }

            var noise = Hash(line);
            var x = 24 + (int)(noise % 24);
            var limit = width - 40 - (int)((noise >> 8) % 160);

            while (x < limit)
            {
                noise = Hash(noise);
                var wordLength = 3 + (int)(noise % 8);

                for (var glyph = 0; glyph < wordLength; glyph++)
                {
                    var glyphLeft = x + (glyph * 9);
                    if (glyphLeft + 7 > limit)
                    {
                        break;
                    }

                    var shade = 20 + (int)((Hash(noise + (uint)glyph)) % 70);
                    using var glyphBrush = new SolidBrush(
                        Color.FromArgb(255, shade, shade, Math.Min(255, shade + 8)));
                    using var edgeBrush = new SolidBrush(
                        Color.FromArgb(
                            255,
                            Math.Min(255, shade + 90),
                            Math.Min(255, shade + 90),
                            Math.Min(255, shade + 98)));
                    graphics.FillRectangle(glyphBrush, glyphLeft, top + 6, 7, 11);
                    graphics.FillRectangle(edgeBrush, glyphLeft, top + 6, 7, 1);
                    graphics.FillRectangle(edgeBrush, glyphLeft + 6, top + 6, 1, 11);
                }

                x += (wordLength * 9) + 8;
            }
        }

        return content;
    }

    private static uint Hash(int value) => Hash((uint)value + 0x9E3779B9u);

    private static uint Hash(uint value)
    {
        unchecked
        {
            value ^= value >> 15;
            value *= 0x2C1B3C6Du;
            value ^= value >> 12;
            value *= 0x297A2D39u;
            value ^= value >> 15;
            return value;
        }
    }

    private static Bitmap Crop(Bitmap source, int top, int height)
    {
        return source.Clone(
            new Rectangle(0, top, source.Width, height),
            PixelFormat.Format32bppPArgb);
    }
}
