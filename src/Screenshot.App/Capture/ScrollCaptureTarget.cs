namespace Screenshot.App.Capture;

public sealed record ScrollCaptureTarget(
    IntPtr WindowHandle,
    IntPtr ScrollTargetHandle,
    ScreenRegion CaptureRegion,
    bool SupportsVerticalScroll);
