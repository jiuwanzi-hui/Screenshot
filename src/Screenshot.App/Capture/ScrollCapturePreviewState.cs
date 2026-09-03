using System.Windows.Media.Imaging;

namespace Screenshot.App.Capture;

public sealed record ScrollCapturePreviewState(
    BitmapSource Preview,
    int FrameCount,
    int AddedAboveFrameCount,
    int AddedBelowFrameCount,
    int PixelWidth,
    int PixelHeight);
