using System.Windows.Media.Imaging;

namespace Screenshot.App.Capture;

public sealed record ScrollCapturePreviewState(
    BitmapSource Preview,
    int FrameCount,
    int AddedAboveFrameCount,
    int AddedBelowFrameCount,
    int PixelWidth,
    int PixelHeight)
{
    public bool? IsTrackingLost { get; init; }
    public bool HasEstimatedBlankSpacing { get; init; }
    public bool IsCrossingBlank { get; init; }
}
