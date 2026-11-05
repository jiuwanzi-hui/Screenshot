using System.Buffers.Binary;
using System.Runtime.InteropServices;
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
    private const uint ClipboardFormatDib = 8;
    private const int BitmapInfoHeaderSize = 40;

    public static async Task SetImageAsync(
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

        await NativeClipboardWriter.WriteAsync(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                SetNativeClipboardDib(image, cancellationToken);
            },
            cancellationToken).ConfigureAwait(false);
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

        using var payload = new ClipboardPayload(ClipboardFormatDib, dibBytes, memory =>
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
        });
        NativeClipboardWriter.Publish(cancellationToken, payload);
    }
}
