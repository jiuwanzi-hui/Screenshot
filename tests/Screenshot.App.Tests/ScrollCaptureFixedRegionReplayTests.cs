using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Screenshot.App.Capture;

namespace Screenshot.App.Tests;

/// <summary>Opt-in pixel replay from a local long image; never captures or drives the desktop.</summary>
public sealed class ScrollCaptureFixedRegionReplayTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private sealed class ReferenceImageTheoryAttribute : TheoryAttribute
    {
        public ReferenceImageTheoryAttribute()
        {
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SNAPCUT_FIXED_REGION_REFERENCE")))
                Skip = "Set SNAPCUT_FIXED_REGION_REFERENCE to a local long PNG with a 58-row header and 18-row footer.";
        }
    }

    [ReferenceImageTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void RealChatImageKeepsEveryRowAcrossBothDirections(bool automatic)
    {
        const int initialTop = 1500;
        const int bodyHeight = 570;
        using var reference = new Bitmap(Environment.GetEnvironmentVariable("SNAPCUT_FIXED_REGION_REFERENCE")!);
        using var initial = CreateFrame(reference, initialTop, bodyHeight);
        using var manual = ScrollCaptureComposer.CreateForManualCapture();
        using var controlled = new ControlledScrollCaptureComposer();
        if (automatic) controlled.Initialize(initial, ScrollCaptureOptions.Default);
        else manual.TryAddFrame(initial, ScrollCaptureOptions.Default, out _);
        var durations = new List<double>();
        var minimum = initialTop;
        var maximum = initialTop;
        var legs = new[]
        {
            Enumerable.Range(1, 12).Select(i => initialTop + i * 100),
            Enumerable.Range(1, 10).Select(i => initialTop - i * 100),
        };
        for (var leg = 0; leg < legs.Length; leg++)
        {
            if (leg == 1)
            {
                if (automatic) controlled.BeginUpwardExtension(initial, ScrollCaptureOptions.Default);
                else
                {
                    for (var p = maximum - 100; p >= initialTop; p -= 100)
                    {
                        using var frame = CreateFrame(reference, p, bodyHeight);
                        manual.TryAddFrame(frame, ScrollCaptureOptions.Default, out _);
                        Assert.Equal(p - initialTop, manual.CurrentFrameTop);
                    }
                }
            }
            foreach (var position in legs[leg])
            {
                using var frame = CreateFrame(reference, position, bodyHeight);
                var timer = Stopwatch.StartNew();
                Assert.True(automatic
                    ? leg == 0 ? controlled.TryAddDown(frame, ScrollCaptureOptions.Default, 100)
                        : controlled.TryAddUp(frame, ScrollCaptureOptions.Default, 100)
                    : manual.TryAddFrame(frame, ScrollCaptureOptions.Default, out _),
                    $"Position {position}: {(automatic ? controlled.LastRejectReason : manual.LastRejectReason)}");
                using var preview = automatic ? controlled.ComposeLivePreview(360, 1100) : manual.ComposeLivePreview(360, 1100);
                durations.Add(timer.Elapsed.TotalMilliseconds);
                minimum = Math.Min(minimum, position);
                maximum = Math.Max(maximum, position);
                Assert.Equal(maximum - minimum + bodyHeight + 76, automatic ? controlled.OutputHeight : manual.OutputHeight);
            }
        }
        Assert.Equal(58, automatic ? controlled.FixedTopRows : manual.FixedTopRows);
        Assert.Equal(18, automatic ? controlled.FixedBottomRows : manual.FixedBottomRows);
        using var expected = CreateFrame(reference, minimum, maximum - minimum + bodyHeight);
        using var result = automatic ? controlled.Compose() : manual.Compose();
        ScrollCaptureFixedRegionsTests.AssertPixelsEqual(expected, result);
        durations.Sort();
        output.WriteLine($"{durations.Count} expansions plus preview, median={durations[durations.Count / 2]:F1} ms, " +
            $"P95={durations[(int)(durations.Count * .95)]:F1} ms, max={durations[^1]:F1} ms.");
    }

    private static Bitmap CreateFrame(Bitmap reference, int documentTop, int bodyHeight)
    {
        using var header = reference.Clone(new Rectangle(0, 0, reference.Width, 58), PixelFormat.Format32bppPArgb);
        using var footer = reference.Clone(new Rectangle(0, reference.Height - 18, reference.Width, 18), PixelFormat.Format32bppPArgb);
        using var body = reference.Clone(new Rectangle(0, 58 + documentTop, reference.Width, bodyHeight), PixelFormat.Format32bppPArgb);
        var result = new Bitmap(reference.Width, bodyHeight + 76, PixelFormat.Format32bppPArgb);
        using var graphics = Graphics.FromImage(result);
        graphics.CompositingMode = CompositingMode.SourceCopy;
        graphics.DrawImageUnscaled(header, 0, 0);
        graphics.DrawImageUnscaled(body, 0, 58);
        graphics.DrawImageUnscaled(footer, 0, 58 + bodyHeight);
        return result;
    }
}
