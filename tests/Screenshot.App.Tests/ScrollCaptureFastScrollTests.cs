using System.Drawing;
using System.Drawing.Imaging;
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
