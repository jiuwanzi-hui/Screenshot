using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using Screenshot.App.Capture;
using Xunit.Abstractions;

namespace Screenshot.App.Tests;

public sealed class AutomaticWidePageTests(ITestOutputHelper output)
{
    [ScrollSampleFact]
    public void WideChatPageKeepsEveryRowWithoutRecoveryScrolls()
    {
        using var original = new Bitmap(Environment.GetEnvironmentVariable("SNAPCUT_SCROLL_SAMPLE")!);
        // The attachment is an already-stitched result with a corrupt right
        // edge. Use only its uncorrupted page as deterministic source pixels.
        using var document = original.Clone(new Rectangle(0, 0, 2050, original.Height), PixelFormat.Format32bppPArgb);
        using var composer = new ControlledScrollCaptureComposer();
        const int height = 800;
        var options = ScrollCaptureOptions.Default with { MinimumNewRows = 1 };
        using var initial = document.Clone(new Rectangle(0, 0, document.Width, height), PixelFormat.Format32bppPArgb);
        composer.Initialize(initial, options);
        var timer = Stopwatch.StartNew();
        var top = 0;
        var count = 0;
        while (top < document.Height - height)
        {
            var delta = Math.Min(new[] { 31, 74, 47, 92 }[count % 4], document.Height - height - top);
            top += delta;
            using var frame = document.Clone(new Rectangle(0, top, document.Width, height), PixelFormat.Format32bppPArgb);
            var accepted = composer.TryAddDown(frame, options, expectedRows: delta, maximumAcceptedNewRows: 250);
            Assert.True(accepted, $"top={top}, delta={delta}, reason={composer.LastRejectReason}, move={composer.LastFrameMovementRows}");
            Assert.Equal(delta, composer.LastFrameMovementRows);
            count++;
        }
        using var result = composer.Compose();
        output.WriteLine($"{count} frames in {timer.Elapsed.TotalSeconds:F3}s; {result.Width}x{result.Height}");
        ScrollCaptureFixedRegionsTests.AssertPixelsEqual(document, result);
    }
}

internal sealed class ScrollSampleFactAttribute : FactAttribute
{
    public ScrollSampleFactAttribute()
    {
        if (!File.Exists(Environment.GetEnvironmentVariable("SNAPCUT_SCROLL_SAMPLE")))
            Skip = "Requires the supplied wide-chat screenshot replay fixture.";
    }
}
