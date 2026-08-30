using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PixelFormat = System.Drawing.Imaging.PixelFormat;

namespace Screenshot.App.Capture;

public sealed class CapturedImage : IDisposable
{
    private readonly object _previewSync = new();
    private BitmapSource? _preview;
    private bool _disposed;

    public CapturedImage(Bitmap bitmap, ScreenRegion? sourceRegion = null)
    {
        ArgumentNullException.ThrowIfNull(bitmap);

        Bitmap = bitmap;
        SourceRegion = sourceRegion;
    }

    public Bitmap Bitmap { get; }

    /// <summary>
    /// Creates the WPF copy only when a UI or clipboard operation actually
    /// needs it. Keeping a GDI bitmap and a BitmapSource for every intermediate
    /// capture doubles the peak memory of long screenshots.
    /// </summary>
    public BitmapSource Preview
    {
        get
        {
            lock (_previewSync)
            {
                return _preview ??= ToBitmapSource(Bitmap);
            }
        }
    }

    /// <summary>
    /// Builds the WPF bitmap off the UI thread so the first Preview access
    /// (history, clipboard, or editor) is only a frozen-source hand-off.
    /// </summary>
    public BitmapSource WarmPreview()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return Preview;
    }

    public ScreenRegion? SourceRegion { get; }

    public CapturedImage Clone()
    {
        lock (_previewSync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return new CapturedImage((Bitmap)Bitmap.Clone(), SourceRegion);
        }
    }

    public static CapturedImage FromBitmapSource(
        BitmapSource bitmapSource,
        ScreenRegion? sourceRegion = null)
    {
        ArgumentNullException.ThrowIfNull(bitmapSource);

        using var stream = new MemoryStream();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmapSource));
        encoder.Save(stream);
        stream.Position = 0;
        using var decodedBitmap = new Bitmap(stream);
        return new CapturedImage(new Bitmap(decodedBitmap), sourceRegion);
    }

    /// <summary>
    /// Converts a GDI bitmap into a frozen WPF bitmap without a PNG round-trip.
    /// Used by the live scroll-capture preview path where encoding every sample
    /// would dominate CPU cost on tall composites.
    /// </summary>
    public static BitmapSource ToBitmapSource(Bitmap bitmap)
    {
        ArgumentNullException.ThrowIfNull(bitmap);

        Bitmap? converted = null;
        var source = bitmap;

        if (bitmap.PixelFormat != PixelFormat.Format32bppPArgb &&
            bitmap.PixelFormat != PixelFormat.Format32bppArgb)
        {
            converted = new Bitmap(
                bitmap.Width,
                bitmap.Height,
                PixelFormat.Format32bppPArgb);
            using var graphics = Graphics.FromImage(converted);
            graphics.DrawImageUnscaled(bitmap, 0, 0);
            source = converted;
        }

        try
        {
            var rectangle = new Rectangle(0, 0, source.Width, source.Height);
            var data = source.LockBits(
                rectangle,
                ImageLockMode.ReadOnly,
                source.PixelFormat);

            try
            {
                var bytes = data.Stride * source.Height;
                var pixels = new byte[bytes];
                Marshal.Copy(data.Scan0, pixels, 0, bytes);
                var pixelFormat = source.PixelFormat == PixelFormat.Format32bppPArgb
                    ? PixelFormats.Pbgra32
                    : PixelFormats.Bgra32;
                var bitmapSource = BitmapSource.Create(
                    source.Width,
                    source.Height,
                    dpiX: 96,
                    dpiY: 96,
                    pixelFormat,
                    palette: null,
                    pixels,
                    data.Stride);
                bitmapSource.Freeze();
                return bitmapSource;
            }
            finally
            {
                source.UnlockBits(data);
            }
        }
        finally
        {
            converted?.Dispose();
        }
    }

    public void Dispose()
    {
        lock (_previewSync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Bitmap.Dispose();
        }
    }

    public CapturedImage CreateScaled(int percent)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        percent = Math.Clamp(percent, 25, 200);
        if (percent == 100)
        {
            return Clone();
        }

        var width = Math.Max(1, (int)Math.Round(Bitmap.Width * percent / 100d));
        var height = Math.Max(1, (int)Math.Round(Bitmap.Height * percent / 100d));
        var scaled = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
        var dpiX = Bitmap.HorizontalResolution > 0 ? Bitmap.HorizontalResolution : 96;
        var dpiY = Bitmap.VerticalResolution > 0 ? Bitmap.VerticalResolution : 96;
        scaled.SetResolution(dpiX, dpiY);
        using (var graphics = Graphics.FromImage(scaled))
        {
            graphics.CompositingMode = CompositingMode.SourceCopy;
            graphics.Clear(System.Drawing.Color.Transparent);
            graphics.CompositingMode = CompositingMode.SourceOver;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.SmoothingMode = SmoothingMode.HighQuality;
            graphics.DrawImage(
                Bitmap,
                new Rectangle(0, 0, width, height),
                new Rectangle(0, 0, Bitmap.Width, Bitmap.Height),
                GraphicsUnit.Pixel);
        }

        return new CapturedImage(scaled, SourceRegion);
    }
}
