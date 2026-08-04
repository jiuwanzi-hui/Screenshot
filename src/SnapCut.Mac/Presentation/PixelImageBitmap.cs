using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using SnapCut.Core;

namespace SnapCut.Mac.Presentation;

internal static class PixelImageBitmap
{
    public static WriteableBitmap Create(PixelImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        var bitmap = new WriteableBitmap(
            new PixelSize(image.Width, image.Height),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Opaque);
        using var framebuffer = bitmap.Lock();
        for (var y = 0; y < image.Height; y++)
        {
            Marshal.Copy(
                image.Pixels,
                y * image.Stride,
                IntPtr.Add(framebuffer.Address, y * framebuffer.RowBytes),
                image.Stride);
        }

        return bitmap;
    }
}
