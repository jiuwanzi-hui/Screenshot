using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Screenshot.App.Core;

namespace Screenshot.App.Capture;

public sealed class CaptureHistoryService
{
    private const double MaximumThumbnailEdgeLength = 160;
    private readonly string _historyDirectory;
    private readonly object _pathSync = new();
    private readonly HashSet<string> _knownPaths = new(
        StringComparer.OrdinalIgnoreCase);

    public CaptureHistoryService(string? historyDirectory = null)
    {
        _historyDirectory = historyDirectory ??
            AppMetadata.HistoryCacheDirectoryPath;
    }

    public ObservableCollection<CaptureHistoryItem> Items { get; } = [];

    public bool PersistsAcrossRestarts { get; private set; }

    public void ConfigurePersistence(bool enabled)
    {
        PersistsAcrossRestarts = enabled;
        TrimToCapacity(AppSettings.MaximumHistoryItems);
    }

    public CaptureHistoryItem? Add(CapturedImage capturedImage, int capacity)
    {
        ArgumentNullException.ThrowIfNull(capturedImage);

        capacity = Math.Clamp(capacity, 0, AppSettings.MaximumHistoryItems);
        if (capacity == 0)
        {
            return null;
        }

        var fullImage = capturedImage.Preview;
        var capturedAt = DateTimeOffset.Now;
        var imagePath = CreateCachePath(capturedAt);
        var item = new CaptureHistoryItem(
            CreateThumbnail(fullImage),
            fullImage,
            capturedAt,
            capturedImage.Bitmap.Width,
            capturedImage.Bitmap.Height,
            imagePath);
        RememberPath(imagePath);
        Items.Insert(0, item);
        TrimToCapacity(capacity);
        BeginOffload(item, fullImage, imagePath);
        return item;
    }

    internal IReadOnlyList<CaptureHistoryItem> LoadPersistedItems(int capacity)
    {
        capacity = Math.Clamp(capacity, 0, AppSettings.MaximumHistoryItems);
        if (capacity == 0 || !Directory.Exists(_historyDirectory))
        {
            return [];
        }

        var loaded = new List<CaptureHistoryItem>(capacity);
        IEnumerable<FileInfo> files;
        try
        {
            files = new DirectoryInfo(_historyDirectory)
                .EnumerateFiles("history-*.png", SearchOption.TopDirectoryOnly)
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .ToArray();
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }

        foreach (var file in files)
        {
            if (IsKnownPath(file.FullName))
            {
                continue;
            }

            if (loaded.Count >= capacity)
            {
                DeleteCacheFile(file.FullName);
                continue;
            }

            try
            {
                using var stream = file.OpenRead();
                var decoder = BitmapDecoder.Create(
                    stream,
                    BitmapCreateOptions.PreservePixelFormat,
                    BitmapCacheOption.OnLoad);
                var fullImage = decoder.Frames[0];
                fullImage.Freeze();
                loaded.Add(new CaptureHistoryItem(
                    CreateThumbnail(fullImage),
                    file.FullName,
                    new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero)
                        .ToLocalTime(),
                    fullImage.PixelWidth,
                    fullImage.PixelHeight));
            }
            catch (Exception exception) when (
                exception is IOException
                    or UnauthorizedAccessException
                    or ArgumentException
                    or NotSupportedException)
            {
                DeleteCacheFile(file.FullName);
            }
        }

        return loaded;
    }

    internal void MergePersistedItems(
        IEnumerable<CaptureHistoryItem> items,
        int capacity)
    {
        if (!PersistsAcrossRestarts)
        {
            return;
        }

        foreach (var item in items)
        {
            var imagePath = item.ImagePath;
            if (imagePath is null || !RememberPath(imagePath))
            {
                continue;
            }

            var insertionIndex = 0;
            while (insertionIndex < Items.Count &&
                   Items[insertionIndex].CapturedAt >= item.CapturedAt)
            {
                insertionIndex++;
            }

            Items.Insert(insertionIndex, item);
        }

        TrimToCapacity(capacity);
    }

    public bool Remove(CaptureHistoryItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (!Items.Remove(item))
        {
            return false;
        }

        var imagePath = item.MarkRemoved();
        ForgetPath(imagePath);
        DeleteCacheFile(imagePath);
        return true;
    }

    public void Clear()
    {
        foreach (var item in Items.ToArray())
        {
            _ = Remove(item);
        }
    }

    /// <summary>
    /// Removes cached screenshots when cross-restart history is disabled.
    /// </summary>
    public static void CleanCacheDirectory(string? historyDirectory = null)
    {
        try
        {
            var directory = historyDirectory ?? AppMetadata.HistoryCacheDirectoryPath;
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
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
    private void BeginOffload(
        CaptureHistoryItem item,
        BitmapSource fullImage,
        string imagePath)
    {
        _ = Task.Run(() =>
        {
            try
            {
                Directory.CreateDirectory(_historyDirectory);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(fullImage));

                using (var stream = File.Create(imagePath))
                {
                    encoder.Save(stream);
                }

                if (!item.CompleteOffload(imagePath))
                {
                    File.Delete(imagePath);
                }
                else if (PersistsAcrossRestarts)
                {
                    PruneCacheDirectory(
                        AppSettings.MaximumHistoryItems,
                        _historyDirectory);
                }
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

    private string CreateCachePath(DateTimeOffset capturedAt)
    {
        var fileName = string.Create(
            CultureInfo.InvariantCulture,
            $"history-{capturedAt:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}.png");
        return Path.Combine(_historyDirectory, fileName);
    }

    internal static void PruneCacheDirectory(
        int capacity,
        string? historyDirectory = null)
    {
        capacity = Math.Clamp(capacity, 0, AppSettings.MaximumHistoryItems);
        var directory = historyDirectory ?? AppMetadata.HistoryCacheDirectoryPath;
        if (!Directory.Exists(directory))
        {
            return;
        }

        try
        {
            foreach (var file in new DirectoryInfo(directory)
                         .EnumerateFiles(
                             "history-*.png",
                             SearchOption.TopDirectoryOnly)
                         .OrderByDescending(file => file.LastWriteTimeUtc)
                         .Skip(capacity))
            {
                try
                {
                    file.Delete();
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private void TrimToCapacity(int capacity)
    {
        capacity = Math.Clamp(capacity, 0, AppSettings.MaximumHistoryItems);
        while (Items.Count > capacity)
        {
            var evicted = Items[^1];
            Items.RemoveAt(Items.Count - 1);
            var imagePath = evicted.MarkRemoved();
            ForgetPath(imagePath);
            DeleteCacheFile(imagePath);
        }
    }

    private bool RememberPath(string imagePath)
    {
        lock (_pathSync)
        {
            return _knownPaths.Add(imagePath);
        }
    }

    private void ForgetPath(string? imagePath)
    {
        if (imagePath is null)
        {
            return;
        }

        lock (_pathSync)
        {
            _knownPaths.Remove(imagePath);
        }
    }

    private bool IsKnownPath(string imagePath)
    {
        lock (_pathSync)
        {
            return _knownPaths.Contains(imagePath);
        }
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
        var longestEdge = Math.Max(source.PixelWidth, source.PixelHeight);
        var scale = longestEdge > MaximumThumbnailEdgeLength
            ? MaximumThumbnailEdgeLength / longestEdge
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
