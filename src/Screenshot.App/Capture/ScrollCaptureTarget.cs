namespace Screenshot.App.Capture;

public sealed record ScrollCaptureTarget(
    IntPtr WindowHandle,
    IntPtr ScrollTargetHandle,
    ScreenRegion CaptureRegion,
    bool SupportsVerticalScroll)
{
    public Func<ScreenRegion?>? PreviewRegionProvider { get; init; }

    internal Func<ScreenRegion>? ScrollInputRegionProvider { get; init; }

    internal ScrollCaptureContext? Context { get; init; }

    internal ScrollCaptureFrameSource? FrameSource { get; init; }

    internal bool AllowContextCapture { get; init; } = true;

    internal IReadOnlyList<ScreenRegion> BorderRegions { get; init; } = [];

    // Borrowed from the coordinator for this session, to preserve fixed pixels
    // outside the target client (for example the caption at a screen edge).
    internal (System.Drawing.Bitmap Image, ScreenRegion Region)? BorderBackdrop { get; init; }
}
