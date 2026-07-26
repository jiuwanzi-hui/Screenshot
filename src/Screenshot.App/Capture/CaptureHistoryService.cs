using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Screenshot.App.Core;

namespace Screenshot.App.Capture;

public sealed class CaptureHistoryService
{
    public ObservableCollection<CaptureHistoryItem> Items { get; } = [];

    public CaptureHistoryItem? Add(CapturedImage capturedImage, int capacity)
    {
        ArgumentNullException.ThrowIfNull(capturedImage);

        if (capacity <= 0)
        {
            return null;
        }

        var fullImage = capturedImage.Preview;
        var item = new CaptureHistoryItem(
            CreateThumbnail(fullImage),
            fullImage,
            DateTimeOffset.Now,
            capturedImage.Bitmap.Width,
            capturedImage.Bitmap.Height);
        Items.Insert(0, item);

        while (Items.Count > capacity)
        {
            var evicted = Items[^1];
            Items.RemoveAt(Items.Count - 1);
            DeleteCacheFile(evicted.TakeImagePath());
        }

        BeginOffload(item, fullImage);
        return item;
    }

    /// <summary>
    /// Removes cache files left behind by earlier sessions. History is
    /// session-scoped, so anything on disk at startup is stale.
    /// </summary>
    public static void CleanCacheDirectory()
    {
        try
        {
            if (Directory.Exists(AppMetadata.HistoryCacheDirectoryPath))
            {
                Directory.Delete(AppMetadata.HistoryCacheDirectoryPath, true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Writes the full image to the history cache on a background thread and
    /// then drops the in-memory copy. The source is frozen, so encoding off
    /// the UI thread is safe; on any failure the entry simply keeps its
    /// in-memory image.
    /// </summary>
    private static void BeginOffload(
        CaptureHistoryItem item,
        BitmapSource fullImage)
    {
        _ = Task.Run(() =>
        {
            try
            {
                Directory.CreateDirectory(AppMetadata.HistoryCacheDirectoryPath);
                var fileName = string.Create(
                    CultureInfo.InvariantCulture,
                    $"history-{item.CapturedAt:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}.png");
                var imagePath = Path.Combine(
                    AppMetadata.HistoryCacheDirectoryPath,
                    fileName);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(fullImage));

                using (var stream = File.Create(imagePath))
                {
                    encoder.Save(stream);
                }

                item.CompleteOffload(imagePath);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
            catch (NotSupportedException)
            {
            }
        });
    }

    private static void DeleteCacheFile(string? imagePath)
    {
        if (imagePath is null)
        {
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                File.Delete(imagePath);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        });
    }

    private static WriteableBitmap CreateThumbnail(BitmapSource source)
    {
        const double maximumEdgeLength = 240;
        var longestEdge = Math.Max(source.PixelWidth, source.PixelHeight);
        var scale = longestEdge > maximumEdgeLength
            ? maximumEdgeLength / longestEdge
            : 1;
        var transformed = new TransformedBitmap(
            source,
            new ScaleTransform(scale, scale));
        // TransformedBitmap keeps the full-size source alive through its
        // Source property. Copying the scaled pixels detaches the thumbnail
        // so the full image can leave memory once it is offloaded to disk.
        var thumbnail = new WriteableBitmap(transformed);
        thumbnail.Freeze();

        return thumbnail;
    }
}
