using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Screenshot.App.Capture;

// DIB storage belongs to GDI, not the managed/WPF image caches. Deleting the
// bitmap returns the full desktop allocation when selection has been handed off.
internal sealed class NativeDesktopFrame : IDisposable
{
    private static int _capturing;
    private IntPtr _bitmap;
    private IntPtr _previousBitmap;
    internal IntPtr DeviceContext { get; private set; }
    internal IntPtr Pixels { get; private set; }
    internal ScreenRegion Region { get; }
    internal int Stride => checked(Region.Width * 4);

    internal NativeDesktopFrame(ScreenRegion region)
    {
        if (region.Width is <= 0 or > 65536 || region.Height is <= 0 or > 65536 ||
            (long)region.Width * region.Height > 134_217_728)
            throw new ArgumentOutOfRangeException(nameof(region));
        Region = region;
        try
        {
            // A DC created with NULL belongs to the creating thread and dies
            // with our one-shot capture thread. Use a real display DC so the
            // UI thread can paint from this DIB after acquisition has returned.
            var display = GetDC(IntPtr.Zero);
            if (display == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            try { DeviceContext = CreateCompatibleDC(display); }
            finally { _ = ReleaseDC(IntPtr.Zero, display); }
            var info = new BitmapInfo { Size = 40, Width = region.Width, Height = -region.Height,
                Planes = 1, Bits = 32 };
            _bitmap = CreateDIBSection(DeviceContext, ref info, 0, out var pixels, IntPtr.Zero, 0);
            Pixels = pixels;
            if (DeviceContext == IntPtr.Zero || _bitmap == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error());
            _previousBitmap = SelectObject(DeviceContext, _bitmap);
        }
        catch { Dispose(); throw; }
    }

    internal static async Task<NativeDesktopFrame> CaptureAsync(ScreenRegion region, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (Interlocked.CompareExchange(ref _capturing, 1, 0) != 0)
            throw new InvalidOperationException("显示设备仍在结束上一次采集，请稍后重试。");
        Task<NativeDesktopFrame> work;
        try { work = Task.Factory.StartNew(() =>
        {
            NativeDesktopFrame? frame = null;
            try
            {
                token.ThrowIfCancellationRequested();
                ResidentStartupFeedback.DismissBeforeCapture();
                _ = DwmFlush(); // Parent chrome has just been hidden.
                token.ThrowIfCancellationRequested();
                frame = new NativeDesktopFrame(region);
                var screen = GetDC(IntPtr.Zero);
                if (screen == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
                try
                {
                    if (!BitBlt(frame.DeviceContext, 0, 0, region.Width, region.Height,
                        screen, region.X, region.Y, 0x40CC0020) &&
                        !BitBlt(frame.DeviceContext, 0, 0, region.Width, region.Height,
                        screen, region.X, region.Y, 0x00CC0020))
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    _ = GdiFlush();
                }
                finally { _ = ReleaseDC(IntPtr.Zero, screen); }
                token.ThrowIfCancellationRequested();
                return frame;
            }
            catch { frame?.Dispose(); throw; }
            finally { Volatile.Write(ref _capturing, 0); }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default); }
        catch { Volatile.Write(ref _capturing, 0); throw; }
        try { return await work.WaitAsync(TimeSpan.FromSeconds(5), token).ConfigureAwait(false); }
        catch
        {
            _ = ReleaseLateAsync(work);
            throw;
        }
    }

    private static async Task ReleaseLateAsync(Task<NativeDesktopFrame> task)
    {
        try { using var frame = await task.ConfigureAwait(false); }
        catch (Exception) { }
    }

    internal uint PixelAt(int x, int y)
    {
        ObjectDisposedException.ThrowIf(Pixels == IntPtr.Zero, this);
        x = Math.Clamp(x - Region.X, 0, Region.Width - 1);
        y = Math.Clamp(y - Region.Y, 0, Region.Height - 1);
        return unchecked((uint)Marshal.ReadInt32(Pixels, checked(y * Stride + x * 4))) & 0xFFFFFF;
    }

    public void Dispose()
    {
        if (DeviceContext != IntPtr.Zero)
        {
            if (_previousBitmap != IntPtr.Zero) _ = SelectObject(DeviceContext, _previousBitmap);
            _ = DeleteDC(DeviceContext);
            DeviceContext = IntPtr.Zero;
        }
        if (_bitmap != IntPtr.Zero) { _ = DeleteObject(_bitmap); _bitmap = IntPtr.Zero; }
        Pixels = IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo
    {
        public uint Size; public int Width, Height; public ushort Planes, Bits;
        public uint Compression, ImageSize; public int XResolution, YResolution;
        public uint ColorsUsed, ColorsImportant, Color;
    }
    [DllImport("gdi32.dll", SetLastError = true)] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfo info, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr value);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr value);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool GdiFlush();
    [DllImport("gdi32.dll", SetLastError = true)] internal static extern bool BitBlt(IntPtr target, int x, int y, int width, int height, IntPtr source, int sx, int sy, uint operation);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("dwmapi.dll")] private static extern int DwmFlush();
}
