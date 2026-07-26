using System.IO;
using System.Windows.Media.Imaging;

namespace Screenshot.App.Capture;

/// <summary>
/// One session-history entry. Only the small detached thumbnail stays in
/// memory: the full image is offloaded to a PNG in the history cache
/// directory as soon as the background encode finishes, because holding a
/// full-resolution bitmap per entry kept hundreds of megabytes resident in
/// an application that is meant to idle in the tray.
/// </summary>
public sealed class CaptureHistoryItem
{
    private readonly object _imageSync = new();
    private BitmapSource? _pendingImage;
    private string? _imagePath;

    public CaptureHistoryItem(
        BitmapSource thumbnail,
        BitmapSource fullImage,
        DateTimeOffset capturedAt,
        int pixelWidth,
        int pixelHeight)
    {
        Thumbnail = thumbnail;
        _pendingImage = fullImage;
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

    /// <summary>
    /// Swaps the retained full image for its on-disk copy. Called from the
    /// background offload once the PNG is fully written.
    /// </summary>
    internal void CompleteOffload(string imagePath)
    {
        lock (_imageSync)
        {
            _imagePath = imagePath;
            _pendingImage = null;
        }
    }

    /// <summary>
    /// Detaches the cache file from the entry so eviction can delete it.
    /// </summary>
    internal string? TakeImagePath()
    {
        lock (_imageSync)
        {
            var path = _imagePath;
            _imagePath = null;
            return path;
        }
    }

    public CapturedImage CreateCapturedImage()
    {
        BitmapSource? pendingImage;
        string? imagePath;

        lock (_imageSync)
        {
            pendingImage = _pendingImage;
            imagePath = _imagePath;
        }

        if (pendingImage is not null)
        {
            return CapturedImage.FromBitmapSource(pendingImage);
        }

        if (imagePath is null)
        {
            throw new InvalidOperationException("这张历史截图已被清理。");
        }

        using var stream = File.OpenRead(imagePath);
        using var decodedBitmap = new System.Drawing.Bitmap(stream);
        return new CapturedImage(new System.Drawing.Bitmap(decodedBitmap));
    }
}
