using System.Windows.Media.Imaging;

namespace Screenshot.App.Capture;

public sealed class CaptureHistoryItem
{
    public CaptureHistoryItem(
        BitmapSource thumbnail,
        DateTimeOffset capturedAt,
        int pixelWidth,
        int pixelHeight)
    {
        Thumbnail = thumbnail;
        CapturedAt = capturedAt;
        PixelWidth = pixelWidth;
        PixelHeight = pixelHeight;
    }

    public BitmapSource Thumbnail { get; }

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
}
