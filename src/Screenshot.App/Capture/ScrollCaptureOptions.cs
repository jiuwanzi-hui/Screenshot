namespace Screenshot.App.Capture;

public sealed record ScrollCaptureOptions(
    int MaximumFrames,
    int ScrollDelta,
    int MinimumOverlapRows,
    double MinimumOverlapConfidence,
    int MinimumNewRows,
    int FrameDelayMilliseconds)
{
    public static ScrollCaptureOptions Default { get; } = new(
        MaximumFrames: 600,
        ScrollDelta: -240,
        MinimumOverlapRows: 20,
        MinimumOverlapConfidence: 0.94,
        MinimumNewRows: 4,
        FrameDelayMilliseconds: 1);
}
