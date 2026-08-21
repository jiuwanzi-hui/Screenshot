namespace SnapCut.Core;

/// <summary>
/// A plain 32-bit BGRA raster the stitching core operates on. Frontends adapt
/// their platform bitmaps (GDI+, CoreGraphics, Skia) to and from this type at
/// the boundary, keeping every algorithm in this library free of platform
/// imaging dependencies.
/// </summary>
public sealed class PixelImage
{
    public PixelImage(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        Width = width;
        Height = height;
        Pixels = new byte[width * height * 4];
    }

    private PixelImage(int width, int height, byte[] pixels)
    {
        Width = width;
        Height = height;
        Pixels = pixels;
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>BGRA bytes, row-major, stride = Width * 4.</summary>
    public byte[] Pixels { get; }

    public int Stride => Width * 4;

    public static PixelImage FromBgra(int width, int height, byte[] pixels)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);

        if (pixels.Length != width * height * 4)
        {
            throw new ArgumentException("像素缓冲区大小与尺寸不匹配。", nameof(pixels));
        }

        return new PixelImage(width, height, pixels);
    }

    public PixelImage Clone()
    {
        var copy = new PixelImage(Width, Height);
        Pixels.CopyTo(copy.Pixels.AsSpan());
        return copy;
    }

    /// <summary>Copies a horizontal band of rows into a new image.</summary>
    public PixelImage CropRows(int top, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(top);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);

        if (top + height > Height)
        {
            throw new ArgumentOutOfRangeException(nameof(height));
        }

        var crop = new PixelImage(Width, height);
        Pixels.AsSpan(top * Stride, height * Stride).CopyTo(crop.Pixels);
        return crop;
    }

    /// <summary>
    /// Copies a horizontal band of rows from this image into
    /// <paramref name="destination"/> at <paramref name="destinationTop"/>,
    /// optionally shifted horizontally (rows are clamped at the edges).
    /// </summary>
    public void BlitRowsTo(
        PixelImage destination,
        int sourceTop,
        int rowCount,
        int destinationTop,
        int horizontalOffset = 0)
    {
        ArgumentNullException.ThrowIfNull(destination);

        if (destination.Width != Width && horizontalOffset == 0)
        {
            // Width-preserving fast path only; mixed widths use the offset path.
        }

        for (var row = 0; row < rowCount; row++)
        {
            var sourceRow = sourceTop + row;
            var destinationRow = destinationTop + row;

            if (sourceRow < 0 ||
                sourceRow >= Height ||
                destinationRow < 0 ||
                destinationRow >= destination.Height)
            {
                continue;
            }

            if (horizontalOffset == 0 && destination.Width == Width)
            {
                Pixels.AsSpan(sourceRow * Stride, Stride)
                    .CopyTo(destination.Pixels.AsSpan(destinationRow * destination.Stride));
                continue;
            }

            for (var x = 0; x < destination.Width; x++)
            {
                var sourceX = Math.Clamp(x - horizontalOffset, 0, Width - 1);
                var from = (sourceRow * Stride) + (sourceX * 4);
                var to = (destinationRow * destination.Stride) + (x * 4);
                destination.Pixels[to] = Pixels[from];
                destination.Pixels[to + 1] = Pixels[from + 1];
                destination.Pixels[to + 2] = Pixels[from + 2];
                destination.Pixels[to + 3] = Pixels[from + 3];
            }
        }
    }

    public void Fill(byte blue, byte green, byte red, byte alpha = 255)
    {
        for (var offset = 0; offset < Pixels.Length; offset += 4)
        {
            Pixels[offset] = blue;
            Pixels[offset + 1] = green;
            Pixels[offset + 2] = red;
            Pixels[offset + 3] = alpha;
        }
    }

    public void FillRect(
        int left,
        int top,
        int width,
        int height,
        byte blue,
        byte green,
        byte red,
        byte alpha = 255)
    {
        var right = Math.Min(Width, left + width);
        var bottom = Math.Min(Height, top + height);

        for (var y = Math.Max(0, top); y < bottom; y++)
        {
            var row = y * Stride;
            for (var x = Math.Max(0, left); x < right; x++)
            {
                var offset = row + (x * 4);
                Pixels[offset] = blue;
                Pixels[offset + 1] = green;
                Pixels[offset + 2] = red;
                Pixels[offset + 3] = alpha;
            }
        }
    }

    /// <summary>
    /// Box-averaged downscale used by viewport fingerprints. Deterministic and
    /// platform-independent, unlike delegating to a GPU or OS scaler.
    /// </summary>
    public PixelImage DownscaleTo(int targetWidth, int targetHeight)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(targetWidth, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(targetHeight, 1);

        var result = new PixelImage(targetWidth, targetHeight);

        for (var ty = 0; ty < targetHeight; ty++)
        {
            var fromY = (int)((long)ty * Height / targetHeight);
            var toY = Math.Max(fromY + 1, (int)((long)(ty + 1) * Height / targetHeight));

            for (var tx = 0; tx < targetWidth; tx++)
            {
                var fromX = (int)((long)tx * Width / targetWidth);
                var toX = Math.Max(fromX + 1, (int)((long)(tx + 1) * Width / targetWidth));
                long b = 0, g = 0, r = 0, a = 0;
                var samples = 0;

                for (var y = fromY; y < toY; y++)
                {
                    var row = y * Stride;
                    for (var x = fromX; x < toX; x++)
                    {
                        var offset = row + (x * 4);
                        b += Pixels[offset];
                        g += Pixels[offset + 1];
                        r += Pixels[offset + 2];
                        a += Pixels[offset + 3];
                        samples++;
                    }
                }

                var target = (ty * result.Stride) + (tx * 4);
                result.Pixels[target] = (byte)(b / samples);
                result.Pixels[target + 1] = (byte)(g / samples);
                result.Pixels[target + 2] = (byte)(r / samples);
                result.Pixels[target + 3] = (byte)(a / samples);
            }
        }

        return result;
    }
}
