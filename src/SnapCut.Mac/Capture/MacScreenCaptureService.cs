using System.Runtime.InteropServices;
using SnapCut.Core;
using SnapCut.Mac.Native;

namespace SnapCut.Mac.Capture;

/// <summary>
/// Captures screen regions into <see cref="PixelImage"/> and writes PNG files,
/// using only C-ABI CoreGraphics/ImageIO calls.
/// </summary>
/// <remarks>
/// Capture uses <c>CGDisplayCreateImageForRect</c>, which requires the Screen
/// Recording permission (系统设置 → 隐私与安全性 → 屏幕录制). On HiDPI
/// displays the returned image is in physical pixels — a 100-point rect on a
/// 2x display yields a 200-pixel-wide frame; the stitching core operates on
/// those physical pixels directly.
/// </remarks>
internal static class MacScreenCaptureService
{
    public static bool HasScreenCaptureAccess()
    {
        return CoreGraphics.CGPreflightScreenCaptureAccess();
    }

    public static bool RequestScreenCaptureAccess()
    {
        return CoreGraphics.CGRequestScreenCaptureAccess();
    }

    /// <summary>Captures a global display-space rect (points) as BGRA pixels.</summary>
    public static PixelImage CaptureRegion(CGRect rect)
    {
        var display = MacDisplayService.SelectDisplayFor(rect);
        var image = CoreGraphics.CGDisplayCreateImageForRect(
            display.DisplayId,
            rect);

        if (image == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                "抓屏失败：请确认已授予屏幕录制权限，且区域位于屏幕内。");
        }

        try
        {
            return ConvertToPixelImage(image);
        }
        finally
        {
            CoreFoundation.CFRelease(image);
        }
    }

    public static PixelImage CaptureAllDisplays()
    {
        var displays = MacDisplayService.GetActiveDisplays();
        if (displays.Count == 0)
        {
            throw new InvalidOperationException("没有可用的显示器。");
        }

        var minLeft = displays.Min(display => display.Bounds.Left);
        var minTop = displays.Min(display => display.Bounds.Top);
        var scale = displays.Max(display => display.Scale);
        var maxRight = displays.Max(display => display.Bounds.Right);
        var maxBottom = displays.Max(display => display.Bounds.Bottom);
        var result = new PixelImage(
            Math.Max(1, (int)Math.Ceiling((maxRight - minLeft) * scale)),
            Math.Max(1, (int)Math.Ceiling((maxBottom - minTop) * scale)));
        result.Fill(0, 0, 0, 255);
        foreach (var display in displays)
        {
            var image = CaptureRegion(display.Bounds);
            var left = Math.Max(0, (int)Math.Round((display.Bounds.Left - minLeft) * scale));
            var top = Math.Max(0, (int)Math.Round((display.Bounds.Top - minTop) * scale));
            for (var y = 0; y < image.Height && top + y < result.Height; y++)
            {
                var width = Math.Min(image.Width, result.Width - left);
                if (width <= 0)
                {
                    continue;
                }
                image.Pixels.AsSpan(y * image.Stride, width * 4).CopyTo(
                    result.Pixels.AsSpan((top + y) * result.Stride + left * 4, width * 4));
            }
        }

        return result;
    }

    /// <summary>Normalizes any 32-bit CGImage byte order to tightly packed BGRA.</summary>
    internal static unsafe PixelImage ConvertToPixelImage(IntPtr cgImage)
    {
        var width = (int)CoreGraphics.CGImageGetWidth(cgImage);
        var height = (int)CoreGraphics.CGImageGetHeight(cgImage);
        var bytesPerRow = (int)CoreGraphics.CGImageGetBytesPerRow(cgImage);
        var bitsPerPixel = (int)CoreGraphics.CGImageGetBitsPerPixel(cgImage);

        if (width < 1 || height < 1 || bitsPerPixel != 32)
        {
            throw new InvalidOperationException(
                $"不支持的抓屏像素格式：{bitsPerPixel}bpp。");
        }

        var bitmapInfo = CoreGraphics.CGImageGetBitmapInfo(cgImage);
        var alphaInfo = bitmapInfo & CoreGraphics.BitmapAlphaInfoMask;
        var byteOrder = bitmapInfo & CoreGraphics.BitmapByteOrderMask;
        var alphaFirst =
            alphaInfo is CoreGraphics.ImageAlphaFirst
                or CoreGraphics.ImageAlphaPremultipliedFirst
                or CoreGraphics.ImageAlphaNoneSkipFirst;
        var littleEndian = byteOrder == CoreGraphics.BitmapByteOrder32Little;

        // Component positions inside each 4-byte pixel, normalized to B,G,R.
        // big-endian/default alphaFirst : A R G B
        // big-endian/default alphaLast  : R G B A
        // little-endian     alphaFirst : B G R A  (display captures land here)
        // little-endian     alphaLast  : A B G R
        int blue, green, red;
        if (littleEndian)
        {
            if (alphaFirst)
            {
                blue = 0;
                green = 1;
                red = 2;
            }
            else
            {
                blue = 1;
                green = 2;
                red = 3;
            }
        }
        else if (alphaFirst)
        {
            blue = 3;
            green = 2;
            red = 1;
        }
        else
        {
            blue = 2;
            green = 1;
            red = 0;
        }

        var provider = CoreGraphics.CGImageGetDataProvider(cgImage);
        var data = CoreGraphics.CGDataProviderCopyData(provider);

        if (data == IntPtr.Zero)
        {
            throw new InvalidOperationException("读取抓屏像素数据失败。");
        }

        try
        {
            var source = (byte*)CoreFoundation.CFDataGetBytePtr(data);
            var available = CoreFoundation.CFDataGetLength(data);

            if (source is null || available < (long)bytesPerRow * height)
            {
                throw new InvalidOperationException("抓屏像素数据不完整。");
            }

            var result = new PixelImage(width, height);
            var pixels = result.Pixels;

            for (var y = 0; y < height; y++)
            {
                var row = source + ((long)y * bytesPerRow);
                var destination = y * result.Stride;

                for (var x = 0; x < width; x++)
                {
                    var pixel = row + (x * 4);
                    var offset = destination + (x * 4);
                    pixels[offset] = pixel[blue];
                    pixels[offset + 1] = pixel[green];
                    pixels[offset + 2] = pixel[red];
                    pixels[offset + 3] = byte.MaxValue;
                }
            }

            return result;
        }
        finally
        {
            CoreFoundation.CFRelease(data);
        }
    }

    /// <summary>Writes a <see cref="PixelImage"/> to a PNG file via ImageIO.</summary>
    public static void SavePng(PixelImage image, string path)
    {
        var fullPath = Path.GetFullPath(path);
        var handle = GCHandle.Alloc(image.Pixels, GCHandleType.Pinned);
        var colorSpace = IntPtr.Zero;
        var provider = IntPtr.Zero;
        var cgImage = IntPtr.Zero;
        var pathString = IntPtr.Zero;
        var url = IntPtr.Zero;
        var typeString = IntPtr.Zero;
        var destination = IntPtr.Zero;

        try
        {
            colorSpace = CoreGraphics.CGColorSpaceCreateDeviceRGB();
            provider = CoreGraphics.CGDataProviderCreateWithData(
                IntPtr.Zero,
                handle.AddrOfPinnedObject(),
                (nuint)image.Pixels.Length,
                IntPtr.Zero);
            cgImage = CoreGraphics.CGImageCreate(
                (nuint)image.Width,
                (nuint)image.Height,
                bitsPerComponent: 8,
                bitsPerPixel: 32,
                bytesPerRow: (nuint)image.Stride,
                colorSpace,
                CoreGraphics.BitmapByteOrder32Little |
                    CoreGraphics.ImageAlphaNoneSkipFirst,
                provider,
                decode: IntPtr.Zero,
                shouldInterpolate: false,
                intent: 0);

            if (cgImage == IntPtr.Zero)
            {
                throw new InvalidOperationException("构建 PNG 图像失败。");
            }

            pathString = CoreFoundation.CFStringCreateWithCString(
                IntPtr.Zero,
                fullPath,
                CoreFoundation.KCFStringEncodingUtf8);
            url = CoreFoundation.CFURLCreateWithFileSystemPath(
                IntPtr.Zero,
                pathString,
                CoreFoundation.KCFURLPosixPathStyle,
                isDirectory: false);
            typeString = CoreFoundation.CFStringCreateWithCString(
                IntPtr.Zero,
                "public.png",
                CoreFoundation.KCFStringEncodingUtf8);
            destination = ImageIO.CGImageDestinationCreateWithURL(
                url,
                typeString,
                count: 1,
                options: IntPtr.Zero);

            if (destination == IntPtr.Zero)
            {
                throw new InvalidOperationException($"无法创建输出文件：{fullPath}");
            }

            ImageIO.CGImageDestinationAddImage(destination, cgImage, IntPtr.Zero);

            if (!ImageIO.CGImageDestinationFinalize(destination))
            {
                throw new InvalidOperationException($"写入 PNG 失败：{fullPath}");
            }
        }
        finally
        {
            ReleaseIfCreated(destination);
            ReleaseIfCreated(typeString);
            ReleaseIfCreated(url);
            ReleaseIfCreated(pathString);
            ReleaseIfCreated(cgImage);
            ReleaseIfCreated(provider);
            ReleaseIfCreated(colorSpace);
            handle.Free();
        }
    }

    private static void ReleaseIfCreated(IntPtr cf)
    {
        if (cf != IntPtr.Zero)
        {
            CoreFoundation.CFRelease(cf);
        }
    }
}
