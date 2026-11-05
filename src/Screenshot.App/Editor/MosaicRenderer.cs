using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PixelFormat = System.Drawing.Imaging.PixelFormat;

namespace Screenshot.App.Editor;

public static class MosaicRenderer
{
    internal static BitmapSource CreateRectanglePreview(Bitmap source, Int32Rect requestedBounds, int blockSize)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(blockSize);
        var bounds = IntersectWithImageBounds(source, requestedBounds);
        if (bounds.Width <= 0 || bounds.Height <= 0)
            throw new ArgumentException("马赛克区域超出图片范围。", nameof(requestedBounds));

        // One texel per mosaic block; the canvas enlarges it with nearest
        // neighbour sampling and clips the partial edge blocks. Resizing needs
        // neither a full-size output buffer nor a checkerboard placeholder.
        var columns = (bounds.Width + blockSize - 1) / blockSize;
        var rows = (bounds.Height + blockSize - 1) / blockSize;
        var stride = checked(columns * 4);
        var pixels = new byte[checked(stride * rows)];
        var row = new byte[checked(bounds.Width * 4)];
        var data = source.LockBits(new Rectangle(bounds.X, bounds.Y, bounds.Width, bounds.Height),
            ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        try
        {
            for (var y = 0; y < rows; y++)
            {
                var sampleY = Math.Min(y * blockSize + blockSize / 2, bounds.Height - 1);
                Marshal.Copy(data.Scan0 + sampleY * data.Stride, row, 0, row.Length);
                for (var x = 0; x < columns; x++)
                {
                    var sampleX = Math.Min(x * blockSize + blockSize / 2, bounds.Width - 1);
                    row.AsSpan(sampleX * 4, 4).CopyTo(pixels.AsSpan(y * stride + x * 4, 4));
                }
            }
        }
        finally
        {
            source.UnlockBits(data);
        }
        var result = BitmapSource.Create(columns, rows, 96, 96, PixelFormats.Pbgra32, null, pixels, stride);
        result.Freeze();
        return result;
    }

    internal static BitmapSource CreateRectangle(Bitmap source, Int32Rect requestedBounds, int blockSize)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(blockSize);
        var bounds = IntersectWithImageBounds(source, requestedBounds);
        if (bounds.Width <= 0 || bounds.Height <= 0)
            throw new ArgumentException("马赛克区域超出图片范围。", nameof(requestedBounds));

        // Process only the selection, in one pixel buffer. Avoid per-pixel GDI
        // calls and PNG encode/decode when committing a large rectangular mask.
        using var region = source.Clone(new Rectangle(bounds.X, bounds.Y, bounds.Width, bounds.Height),
            PixelFormat.Format32bppPArgb);
        var stride = bounds.Width * 4;
        var pixels = new byte[checked(stride * bounds.Height)];
        var data = region.LockBits(new Rectangle(0, 0, bounds.Width, bounds.Height),
            ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        try
        {
            for (var y = 0; y < bounds.Height; y++)
                Marshal.Copy(data.Scan0 + y * data.Stride, pixels, y * stride, stride);
        }
        finally
        {
            region.UnlockBits(data);
        }
        for (var top = 0; top < bounds.Height; top += blockSize)
        for (var left = 0; left < bounds.Width; left += blockSize)
        {
            var sample = Math.Min(top + blockSize / 2, bounds.Height - 1) * stride +
                Math.Min(left + blockSize / 2, bounds.Width - 1) * 4;
            var blue = pixels[sample];
            var green = pixels[sample + 1];
            var red = pixels[sample + 2];
            var alpha = pixels[sample + 3];
            var bottom = Math.Min(top + blockSize, bounds.Height);
            var right = Math.Min(left + blockSize, bounds.Width);
            for (var y = top; y < bottom; y++)
            for (var x = left; x < right; x++)
            {
                var offset = y * stride + x * 4;
                pixels[offset] = blue;
                pixels[offset + 1] = green;
                pixels[offset + 2] = red;
                pixels[offset + 3] = alpha;
            }
        }
        var result = BitmapSource.Create(bounds.Width, bounds.Height, 96, 96,
            PixelFormats.Pbgra32, null, pixels, stride);
        result.Freeze();
        return result;
    }

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
