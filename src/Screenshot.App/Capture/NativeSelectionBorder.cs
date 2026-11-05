using System.Drawing;
using System.Runtime.InteropServices;

namespace Screenshot.App.Capture;

// Four opaque, three-pixel strips instead of a selection-sized alpha bitmap.
// Resizing a 4K selection uploads KB rather than clearing/copying 30+ MB.
internal sealed class NativeSelectionBorder(IntPtr topWindow) : IDisposable
{
    internal const int Thickness = 3;
    private readonly IntPtr[] _windows = [topWindow, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero];
    private readonly NativeBorderBitmap[] _buffers = [new(), new(), new(), new()];
    private IntPtr _owner;
    private Rectangle _bounds;
    private Color _start;
    private Color _end;
    private bool _painted;

    internal static Rectangle[] GetStrips(Rectangle bounds)
    {
        var strips = new Rectangle[4];
        GetStrips(bounds, strips);
        return strips;
    }

    private static void GetStrips(Rectangle bounds, Span<Rectangle> strips)
    {
        var horizontal = Math.Min(Thickness, Math.Max(0, bounds.Height));
        var vertical = Math.Min(Thickness, Math.Max(0, bounds.Width));
        strips[0] = new(bounds.X, bounds.Y, bounds.Width, horizontal);
        strips[1] = new(bounds.X, bounds.Bottom - horizontal, bounds.Width, horizontal);
        strips[2] = new(bounds.X, bounds.Y + horizontal, vertical, Math.Max(0, bounds.Height - 2 * horizontal));
        strips[3] = new(bounds.Right - vertical, bounds.Y + horizontal, vertical, Math.Max(0, bounds.Height - 2 * horizontal));
    }

    public void SetOwner(IntPtr owner)
    {
        _owner = owner;
        foreach (var window in _windows)
            if (window != IntPtr.Zero) _ = SetWindowLongPtrW(window, -8, owner);
    }

    public void Update(Rectangle bounds, Color start, Color end)
    {
        EnsureWindows();
        var repaint = !_painted || bounds.Size != _bounds.Size || start != _start || end != _end;
        Span<Rectangle> strips = stackalloc Rectangle[4];
        GetStrips(bounds, strips);
        if (!repaint && MoveTogether(strips))
        {
            _bounds = bounds;
            return;
        }
        for (var i = 0; i < strips.Length; i++)
        {
            var strip = strips[i];
            if (strip.Width <= 0 || strip.Height <= 0)
            {
                _ = ShowWindow(_windows[i], 0);
                continue;
            }
            if (repaint) Paint(_windows[i], _buffers[i], strip, bounds, start, end);
            else _ = SetWindowPos(_windows[i], IntPtr.Zero, strip.X, strip.Y, 0, 0, 0x15);
            if (!IsWindowVisible(_windows[i])) _ = ShowWindow(_windows[i], 8);
        }
        _bounds = bounds;
        _start = start;
        _end = end;
        _painted = true;
    }

    private bool MoveTogether(ReadOnlySpan<Rectangle> strips)
    {
        // Submit all four moves together, without changing the topmost order
        // or uploading unchanged pixels while the user moves a selected box.
        var batch = BeginDeferWindowPos(_windows.Length);
        if (batch == IntPtr.Zero) return false;
        for (var i = 0; i < strips.Length; i++)
        {
            var strip = strips[i];
            var flags = 0x0215u | (strip.Width > 0 && strip.Height > 0 ? 0x40u : 0x80u);
            batch = DeferWindowPos(batch, _windows[i], IntPtr.Zero, strip.X, strip.Y, 0, 0, flags);
            if (batch == IntPtr.Zero) return false;
        }
        return EndDeferWindowPos(batch);
    }

    public void Hide()
    {
        foreach (var window in _windows)
            if (window != IntPtr.Zero) _ = ShowWindow(window, 0);
    }

    public void EnsureTopmost()
    {
        foreach (var window in _windows)
            if (window != IntPtr.Zero) _ = SetWindowPos(window, new IntPtr(-1), 0, 0, 0, 0, 0x213);
    }

    private void EnsureWindows()
    {
        for (var i = 1; i < _windows.Length; i++)
        {
            if (_windows[i] != IntPtr.Zero) continue;
            _windows[i] = CreateWindowExW(0x080800A8, "STATIC", "", 0x80000000,
                0, 0, 1, 1, _owner, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (_windows[i] == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
        }
    }

    internal bool ExcludeFromScreenCapture()
    {
        EnsureWindows();
        var excluded = true;
        foreach (var window in _windows) excluded &= ScrollCaptureWindowExclusion.Exclude(window);
        return excluded;
    }

    internal IntPtr[] GetWindowHandles()
    {
        EnsureWindows();
        return (IntPtr[])_windows.Clone();
    }

    private static void Paint(IntPtr window, NativeBorderBitmap buffer,
        Rectangle strip, Rectangle selection, Color start, Color end)
    {
        buffer.Paint(strip.Width, strip.Height, strip.X - selection.X, selection.Width, start, end);
        var position = strip.Location;
        var size = strip.Size;
        var origin = Point.Empty;
        var blend = new BlendFunction { Alpha = 255, Format = 1 };
        if (!UpdateLayeredWindow(window, IntPtr.Zero, ref position, ref size, buffer.DeviceContext,
                ref origin, 0, ref blend, 2))
            throw new System.ComponentModel.Win32Exception();
    }

    public void Dispose()
    {
        Hide();
        foreach (var buffer in _buffers) buffer.Dispose();
        // The first HWND belongs to NativeSelectionFrameWindow.
        for (var i = 1; i < _windows.Length; i++)
        {
            if (_windows[i] != IntPtr.Zero) _ = DestroyWindow(_windows[i]);
            _windows[i] = IntPtr.Zero;
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct BlendFunction { public byte Operation; public byte Flags; public byte Alpha; public byte Format; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(uint exStyle, string className, string title, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr SetWindowLongPtrW(IntPtr window, int index, IntPtr value);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr BeginDeferWindowPos(int count);
    [DllImport("user32.dll")] private static extern IntPtr DeferWindowPos(IntPtr batch,
        IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool EndDeferWindowPos(IntPtr batch);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UpdateLayeredWindow(
        IntPtr window, IntPtr destinationDc, ref Point destination, ref Size size, IntPtr sourceDc,
        ref Point source, int key, ref BlendFunction blend, int flags);
}
