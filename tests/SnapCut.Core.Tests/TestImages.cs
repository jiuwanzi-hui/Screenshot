using SnapCut.Core;

namespace SnapCut.Core.Tests;

/// <summary>
/// Deterministic <see cref="PixelImage"/> drawing helpers for the stitching
/// tests. Fixtures and assertions both go through these, so the suites stay
/// self-consistent without any platform imaging dependency.
/// </summary>
internal static class TestImages
{
    /// <summary>Packs an opaque color as 0x00RRGGBB for equality asserts.</summary>
    public static uint Rgb(byte r, byte g, byte b) =>
        ((uint)r << 16) | ((uint)g << 8) | b;

    public static uint GetPixel(PixelImage image, int x, int y)
    {
        var offset = (y * image.Stride) + (x * 4);
        return Rgb(
            image.Pixels[offset + 2],
            image.Pixels[offset + 1],
            image.Pixels[offset]);
    }

    public static void SetPixel(PixelImage image, int x, int y, uint rgb)
    {
        var offset = (y * image.Stride) + (x * 4);
        image.Pixels[offset] = (byte)rgb;
        image.Pixels[offset + 1] = (byte)(rgb >> 8);
        image.Pixels[offset + 2] = (byte)(rgb >> 16);
        image.Pixels[offset + 3] = byte.MaxValue;
    }

    public static void Fill(PixelImage image, uint rgb)
    {
        image.Fill((byte)rgb, (byte)(rgb >> 8), (byte)(rgb >> 16));
    }

    public static void FillRect(
        PixelImage image,
        int left,
        int top,
        int width,
        int height,
        uint rgb)
    {
        image.FillRect(
            left,
            top,
            width,
            height,
            (byte)rgb,
            (byte)(rgb >> 8),
            (byte)(rgb >> 16));
    }

    public static PixelImage Crop(PixelImage source, int top, int height)
    {
        return source.CropRows(top, height);
    }
}
