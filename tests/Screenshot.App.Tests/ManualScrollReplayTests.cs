using System.Drawing;
using System.Drawing.Imaging;
using System.Diagnostics;
using System.IO;
using Screenshot.App.Capture;

namespace Screenshot.App.Tests;

/// <summary>Optional replay of real viewport PNGs, without global input or desktop capture.</summary>
public sealed class ManualScrollReplayTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private sealed class ReplayFactAttribute : FactAttribute
    {
        public ReplayFactAttribute()
        {
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SNAPCUT_REPLAY_FRAMES")))
                Skip = "Set SNAPCUT_REPLAY_FRAMES to a directory containing captured viewport PNGs.";
        }
    }

    [ReplayFact]
    public void RealEditorFramesRemainLocatedAcrossReturns()
    {
        var directory = Environment.GetEnvironmentVariable("SNAPCUT_REPLAY_FRAMES")!;
        using var composer = ScrollCaptureComposer.CreateForManualCapture();
        using var gate = new CapturedFrameGate();
        var files = Directory.GetFiles(directory, "*.png").Order().ToArray();
        Assert.True(files.Length >= 3);
        var initialHeight = 0;
        var durations = new List<double>();
        var positions = new List<string>();
        var consecutiveMisses = 0;
        var maximumConsecutiveMisses = 0;
        foreach (var path in files)
        {
            using var source = new Bitmap(path);
            Assert.Equal(ImageFormat.Png.Guid, source.RawFormat.Guid);
            // Capture tooling may record the whole editor window. The optional
            // rectangle selects the same fixed screen region from every frame.
            var crop = Environment.GetEnvironmentVariable("SNAPCUT_REPLAY_CROP")?.Split(',').Select(int.Parse).ToArray();
            var rectangle = crop is { Length: 4 } ? new Rectangle(crop[0], crop[1], crop[2], crop[3]) :
                new Rectangle(0, 0, source.Width, source.Height);
            using var frame = source.Clone(rectangle, PixelFormat.Format32bppPArgb);
            initialHeight = frame.Height;
            var timer = Stopwatch.StartNew();
            if (!gate.HasChanged(frame)) continue;
            gate.AcceptPending();
            composer.TryAddFrame(frame, ScrollCaptureDirection.Up, ScrollCaptureOptions.Default,
                expectedNewRows: 30, lockDirection: true, out _);
            durations.Add(timer.Elapsed.TotalMilliseconds);
            consecutiveMisses = composer.LastFrameMovementRows is null ? consecutiveMisses + 1 : 0;
            maximumConsecutiveMisses = Math.Max(maximumConsecutiveMisses, consecutiveMisses);
            positions.Add($"{Path.GetFileName(path)},{composer.CurrentFrameTop},{composer.LastRejectReason}");
            Assert.Equal(composer.CapturedContentBottom - composer.CapturedContentTop +
                composer.FixedTopRows + composer.FixedBottomRows, composer.OutputHeight);
            using var preview = composer.ComposeLivePreview(frame.Width, 20000);
            Assert.Equal(composer.OutputHeight, preview.Height);
        }
        Assert.NotNull(composer.LastFrameMovementRows);
        Assert.True(composer.OutputHeight > initialHeight);
        Assert.True(composer.AddedAboveFrameCount > 0);
        Assert.True(composer.AddedBelowFrameCount > 0);
        durations.Sort();
        output.WriteLine($"{files.Length} frames, range={composer.CapturedContentTop}..{composer.CapturedContentBottom}, " +
            $"P95={durations[(int)(durations.Count * .95)]:F1}ms, max={durations[^1]:F1}ms, max consecutive misses={maximumConsecutiveMisses}");
        Assert.True(maximumConsecutiveMisses < 12, $"Lost tracking for {maximumConsecutiveMisses} consecutive frames.");
        if (Environment.GetEnvironmentVariable("SNAPCUT_REPLAY_OUTPUT") is { Length: > 0 } outputPath)
        {
            using var result = composer.Compose();
            result.Save(outputPath, ImageFormat.Png);
            File.WriteAllLines(Path.ChangeExtension(outputPath, ".csv"), positions);
        }
    }

}
