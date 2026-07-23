using System.Collections.ObjectModel;
using System.Windows.Media;
using System.Windows.Media.Imaging;

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

        var item = new CaptureHistoryItem(
            CreateThumbnail(capturedImage.Preview),
            DateTimeOffset.Now,
            capturedImage.Bitmap.Width,
            capturedImage.Bitmap.Height);
        Items.Insert(0, item);

        while (Items.Count > capacity)
        {
            Items.RemoveAt(Items.Count - 1);
        }

        return item;
    }

    private static TransformedBitmap CreateThumbnail(BitmapSource source)
    {
        const double maximumEdgeLength = 240;
        var longestEdge = Math.Max(source.PixelWidth, source.PixelHeight);
        var scale = longestEdge > maximumEdgeLength
            ? maximumEdgeLength / longestEdge
            : 1;
        var thumbnail = new TransformedBitmap(
            source,
            new ScaleTransform(scale, scale));
        thumbnail.Freeze();

        return thumbnail;
    }
}
