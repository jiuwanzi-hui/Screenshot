using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Screenshot.App.Capture;

/// <summary>
/// Copies a frozen WPF image through the native CF_DIB clipboard format.
/// Avoiding WPF's delayed OLE bitmap conversion is important for long images:
/// that conversion can outlive the source and terminates the process on failure.
/// </summary>
public static class ClipboardImageService
{
    private const uint GlobalMoveable = 0x0002;
    private const uint ClipboardFormatDib = 8;
    private const int BitmapInfoHeaderSize = 40;
    private const int ClipboardOpenAttempts = 10;
    private const int ClipboardRetryDelayMilliseconds = 20;

    public static Task SetImageAsync(
        BitmapSource image,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (!image.IsFrozen)
        {
            throw new ArgumentException(
                "剪贴板图片必须是冻结的位图源。",
                nameof(image));
        }

        return Task.Run(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                SetNativeClipboardDib(image, cancellationToken);
            },
            cancellationToken);
    }

    private static void SetNativeClipboardDib(
        BitmapSource image,
        CancellationToken cancellationToken)
    {
        BitmapSource source = image;
        if (source.Format != PixelFormats.Bgra32 &&
            source.Format != PixelFormats.Pbgra32)
        {
            var converted = new FormatConvertedBitmap(
                source,
                PixelFormats.Bgra32,
                destinationPalette: null,
                alphaThreshold: 0);
            converted.Freeze();
            source = converted;
        }

        var width = source.PixelWidth;
        var height = source.PixelHeight;
        var stride = checked(width * 4);
        var pixelBytes = checked(stride * height);
        var dibBytes = checked(BitmapInfoHeaderSize + pixelBytes);
        var header = new byte[BitmapInfoHeaderSize];
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(0, 4), BitmapInfoHeaderSize);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4, 4), width);
        // Positive height is the broadly compatible bottom-up CF_DIB layout.
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8, 4), height);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(12, 2), 1);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(14, 2), 32);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(16, 4), 0);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(20, 4), pixelBytes);

        var memoryHandle = NativeMethods.GlobalAlloc(
            GlobalMoveable,
            (nuint)dibBytes);
        if (memoryHandle == IntPtr.Zero)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "无法分配剪贴板图片内存。");
        }

        try
        {
            var memory = NativeMethods.GlobalLock(memoryHandle);
            if (memory == IntPtr.Zero)
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "无法锁定剪贴板图片内存。");
            }

            try
            {
                Marshal.Copy(header, 0, memory, header.Length);
                var row = new byte[stride];
                for (var sourceRow = 0; sourceRow < height; sourceRow++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    source.CopyPixels(
                        new System.Windows.Int32Rect(0, sourceRow, width, 1),
                        row,
                        stride,
                        0);
                    var destinationRow = height - sourceRow - 1;
                    Marshal.Copy(
                        row,
                        0,
                        IntPtr.Add(
                            memory,
                            BitmapInfoHeaderSize + (destinationRow * stride)),
                        stride);
                }
            }
            finally
            {
                _ = NativeMethods.GlobalUnlock(memoryHandle);
            }

            for (var attempt = 0; attempt < ClipboardOpenAttempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!NativeMethods.OpenClipboard(IntPtr.Zero))
                {
                    if (attempt + 1 < ClipboardOpenAttempts)
                    {
                        Thread.Sleep(ClipboardRetryDelayMilliseconds);
                        continue;
                    }

                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        "剪贴板正被其他程序使用。");
                }

                try
                {
                    if (!NativeMethods.EmptyClipboard())
                    {
                        throw new Win32Exception(
                            Marshal.GetLastWin32Error(),
                            "无法清空剪贴板。");
                    }

                    if (NativeMethods.SetClipboardData(
                            ClipboardFormatDib,
                            memoryHandle) == IntPtr.Zero)
                    {
                        throw new Win32Exception(
                            Marshal.GetLastWin32Error(),
                            "无法写入剪贴板图片。");
                    }

                    // The system owns the allocation after SetClipboardData.
                    memoryHandle = IntPtr.Zero;
                    return;
                }
                finally
                {
                    _ = NativeMethods.CloseClipboard();
                }
            }
        }
        finally
        {
            if (memoryHandle != IntPtr.Zero)
            {
                _ = NativeMethods.GlobalFree(memoryHandle);
            }
        }
    }

    private static class NativeMethods
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr GlobalAlloc(uint flags, nuint bytes);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr GlobalLock(IntPtr memoryHandle);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GlobalUnlock(IntPtr memoryHandle);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr GlobalFree(IntPtr memoryHandle);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool OpenClipboard(IntPtr ownerWindow);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseClipboard();

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool EmptyClipboard();

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetClipboardData(
            uint format,
            IntPtr memoryHandle);
    }
}
