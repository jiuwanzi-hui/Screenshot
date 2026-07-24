using System.Windows.Media.Imaging;

namespace Screenshot.App.Capture;

public sealed class CaptureHistoryItem
{
    public CaptureHistoryItem(
        BitmapSource thumbnail,
        BitmapSource fullImage,
        DateTimeOffset capturedAt,
        int pixelWidth,
        int pixelHeight)
    {
        Thumbnail = thumbnail;
        FullImage = fullImage;
        CapturedAt = capturedAt;
        PixelWidth = pixelWidth;
        PixelHeight = pixelHeight;
    }

    public BitmapSource Thumbnail { get; }

    private BitmapSource FullImage { get; }

    public DateTimeOffset CapturedAt { get; }

    public int PixelWidth { get; }

    public int PixelHeight { get; }

    public bool IsCopied { get; private set; }

    public string? SavedPath { get; private set; }

    public void MarkCopied()
    {
        IsCopied = true;
    }

    public void MarkSaved(string savedPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(savedPath);

        SavedPath = savedPath;
    }

    public CapturedImage CreateCapturedImage()
    {
        return CapturedImage.FromBitmapSource(FullImage);
    }
}
