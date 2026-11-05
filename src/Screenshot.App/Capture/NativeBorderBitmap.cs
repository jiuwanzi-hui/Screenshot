using System.Drawing;
using System.Runtime.InteropServices;

namespace Screenshot.App.Capture;

// Reusable, top-down DIB for a thin border strip. Resizing a selection writes
// pixels into this buffer; it does not create a GDI+ bitmap/DC/HBITMAP per frame.
internal sealed class NativeBorderBitmap : IDisposable
{
    private IntPtr _dc;
    private IntPtr _bitmap;
    private IntPtr _previousBitmap;
    private IntPtr _bits;
    private int[] _pixels = [];
    private int _width;
    private int _height;

    internal IntPtr DeviceContext => _dc;

    internal void Paint(int width, int height, int xOffset, int selectionWidth,
        Color start, Color end)
    {
        EnsureCapacity(width, height);
        var denominator = Math.Max(1, selectionWidth - 1);
        for (var x = 0; x < width; x++)
        {
            var position = Math.Clamp(xOffset + x, 0, denominator);
            var r = start.R + (end.R - start.R) * position / denominator;
            var g = start.G + (end.G - start.G) * position / denominator;
            var b = start.B + (end.B - start.B) * position / denominator;
            _pixels[x] = unchecked((int)0xFF000000) | r << 16 | g << 8 | b;
        }
        for (var y = 1; y < height; y++)
            Array.Copy(_pixels, 0, _pixels, y * _width, width);
        Marshal.Copy(_pixels, 0, _bits, _width * height);
    }

    private void EnsureCapacity(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (_bitmap != IntPtr.Zero && width <= _width && height <= _height) return;
        ReleaseBitmap();
        _width = Math.Max(_width, Capacity(width));
        _height = Math.Max(_height, Capacity(height));
        _pixels = new int[checked(_width * _height)];
        if (_dc == IntPtr.Zero)
        {
            _dc = CreateCompatibleDC(IntPtr.Zero);
            if (_dc == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
        }
        var info = new BitmapInfo
        {
            HeaderSize = 40, Width = _width, Height = -_height,
            Planes = 1, BitCount = 32,
        };
        _bitmap = CreateDIBSection(IntPtr.Zero, ref info, 0, out _bits, IntPtr.Zero, 0);
        if (_bitmap == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
        _previousBitmap = SelectObject(_dc, _bitmap);
    }

    private static int Capacity(int value) => value <= NativeSelectionBorder.Thickness
        ? NativeSelectionBorder.Thickness : checked((value + 255) / 256 * 256);

    private void ReleaseBitmap()
    {
        if (_bitmap == IntPtr.Zero) return;
        _ = SelectObject(_dc, _previousBitmap);
        _ = DeleteObject(_bitmap);
        _bitmap = _bits = _previousBitmap = IntPtr.Zero;
    }

    public void Dispose()
    {
        ReleaseBitmap();
        if (_dc != IntPtr.Zero) _ = DeleteDC(_dc);
        _dc = IntPtr.Zero;
        _pixels = [];
        _width = _height = 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public uint HeaderSize;
        public int Width, Height;
        public ushort Planes, BitCount;
        public uint Compression, ImageSize;
        public int XPelsPerMeter, YPelsPerMeter;
        public uint ColorsUsed, ColorsImportant, Color;
    }

    [DllImport("gdi32.dll", SetLastError = true)] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern IntPtr CreateDIBSection(
        IntPtr dc, ref BitmapInfo info, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr value);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr value);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
}
