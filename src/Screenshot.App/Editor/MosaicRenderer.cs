using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;

namespace Screenshot.App.Editor;

public static class MosaicRenderer
{
    public static BitmapImage Create(Bitmap source, Int32Rect requestedBounds, int blockSize)
    {
        ArgumentNullException.ThrowIfNull(source);

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(blockSize);

        var bounds = IntersectWithImageBounds(source, requestedBounds);

        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            throw new ArgumentException("马赛克区域超出图片范围。", nameof(requestedBounds));
        }

        using var mosaic = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppPArgb);
        using (var graphics = Graphics.FromImage(mosaic))
        {
            graphics.DrawImage(
                source,
                new Rectangle(0, 0, bounds.Width, bounds.Height),
                new Rectangle(bounds.X, bounds.Y, bounds.Width, bounds.Height),
                GraphicsUnit.Pixel);

            for (var top = 0; top < mosaic.Height; top += blockSize)
            {
                for (var left = 0; left < mosaic.Width; left += blockSize)
                {
                    var sampleX = Math.Min(left + (blockSize / 2), mosaic.Width - 1);
                    var sampleY = Math.Min(top + (blockSize / 2), mosaic.Height - 1);
                    using var brush = new SolidBrush(mosaic.GetPixel(sampleX, sampleY));
                    graphics.FillRectangle(
                        brush,
                        left,
                        top,
                        Math.Min(blockSize, mosaic.Width - left),
                        Math.Min(blockSize, mosaic.Height - top));
                }
            }
        }

        using var stream = new MemoryStream();
        mosaic.Save(stream, ImageFormat.Png);
        stream.Position = 0;

        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();

        return image;
    }

    private static Int32Rect IntersectWithImageBounds(Bitmap image, Int32Rect bounds)
    {
        var left = Math.Clamp(bounds.X, 0, image.Width);
        var top = Math.Clamp(bounds.Y, 0, image.Height);
        var right = Math.Clamp(bounds.X + bounds.Width, 0, image.Width);
        var bottom = Math.Clamp(bounds.Y + bounds.Height, 0, image.Height);

        return new Int32Rect(left, top, right - left, bottom - top);
    }
}
