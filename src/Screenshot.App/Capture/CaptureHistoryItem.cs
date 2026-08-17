using System.IO;
using System.Windows.Media.Imaging;

namespace Screenshot.App.Capture;

/// <summary>
/// One screenshot-history entry. Only the small detached thumbnail stays in
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
    private bool _isRemoved;

    public CaptureHistoryItem(
        BitmapSource thumbnail,
        BitmapSource fullImage,
        DateTimeOffset capturedAt,
        int pixelWidth,
        int pixelHeight,
        string? imagePath = null)
    {
        Thumbnail = thumbnail;
        _pendingImage = fullImage;
        _imagePath = imagePath;
        CapturedAt = capturedAt;
        PixelWidth = pixelWidth;
        PixelHeight = pixelHeight;
    }

    internal CaptureHistoryItem(
        BitmapSource thumbnail,
        string imagePath,
        DateTimeOffset capturedAt,
        int pixelWidth,
        int pixelHeight)
    {
        Thumbnail = thumbnail;
        _imagePath = imagePath;
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

    internal string? ImagePath
    {
        get
        {
            lock (_imageSync)
            {
                return _imagePath;
            }
        }
    }

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
    /// Swaps the retained full image for its on-disk copy. Returns false when
    /// the entry was removed while the background PNG encode was running.
    /// </summary>
    internal bool CompleteOffload(string imagePath)
    {
        lock (_imageSync)
        {
            if (_isRemoved)
            {
                return false;
            }

            _imagePath = imagePath;
            _pendingImage = null;
            return true;
        }
    }

    /// <summary>
    /// Detaches the cache file from the entry so eviction can delete it.
    /// </summary>
    internal string? MarkRemoved()
    {
        lock (_imageSync)
        {
            _isRemoved = true;
            _pendingImage = null;
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
            if (_isRemoved)
            {
                throw new InvalidOperationException("这张历史截图已被删除。");
            }

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
