using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.ComponentModel;
using System.Runtime.InteropServices;
using WinForms = System.Windows.Forms;

namespace Screenshot.App.Capture;

public static class ScreenCaptureService
{
    private static readonly Color MonitorDividerColor =
        Color.FromArgb(190, 196, 204);

    public static CapturedImage Capture(ScreenRegion region)
    {
        if (region.IsEmpty)
        {
            throw new ArgumentException("截图区域不能为空。", nameof(region));
        }

        ResidentStartupFeedback.DismissBeforeCapture();
        // The desktop has no alpha channel. GDI/display drivers may leave the
        // reserved byte at zero, especially after a display/session reset.
        // Treating it as PArgb makes both the frozen frame and its HWND
        // hit-testing transparent even though the RGB pixels are valid.
        var bitmap = CreateDesktopBitmap(region);

        try
        {
            using var graphics = Graphics.FromImage(bitmap);
            graphics.CopyFromScreen(region.X, region.Y, 0, 0, bitmap.Size);
            return new CapturedImage(bitmap, region);
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    public static CapturedImage CaptureIncludingLayeredWindows(ScreenRegion region)
    {
        if (region.IsEmpty)
        {
            throw new ArgumentException("截图区域不能为空。", nameof(region));
        }

        ResidentStartupFeedback.DismissBeforeCapture();
        var bitmap = CreateDesktopBitmap(region);

        try
        {
            using var graphics = Graphics.FromImage(bitmap);
            // Graphics.CopyFromScreen rejects combined raster flags before it
            // reaches GDI. Pass SRCCOPY | CAPTUREBLT directly to BitBlt so the
            // compositor's layered menu pixels and shadows are actually read.
            var screenDc = NativeMethods.GetDC(IntPtr.Zero);
            if (screenDc == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error());
            var targetDc = IntPtr.Zero;
            try
            {
                targetDc = graphics.GetHdc();
                if (!NativeMethods.BitBlt(targetDc, 0, 0, region.Width, region.Height,
                    screenDc, region.X, region.Y, 0x00CC0020u | 0x40000000u))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            finally
            {
                if (targetDc != IntPtr.Zero) graphics.ReleaseHdc(targetDc);
                _ = NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
            }
            return new CapturedImage(bitmap, region);
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    internal static Bitmap CreateDesktopBitmap(ScreenRegion region) =>
        new(region.Width, region.Height, PixelFormat.Format32bppRgb);

    private static class NativeMethods
    {
        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr GetDC(IntPtr window);

        [DllImport("user32.dll")]
        public static extern int ReleaseDC(IntPtr window, IntPtr deviceContext);

        [DllImport("gdi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool BitBlt(IntPtr destination, int x, int y, int width, int height,
            IntPtr source, int sourceX, int sourceY, uint operation);
    }

    public static CapturedImage CaptureAllScreens()
    {
        var virtualBounds = VirtualScreen.GetBounds();
        using var snapshot = Capture(virtualBounds);
        var monitors = WinForms.Screen.AllScreens
            .Select(screen => new ScreenRegion(
                screen.Bounds.X,
                screen.Bounds.Y,
                screen.Bounds.Width,
                screen.Bounds.Height))
            .ToArray();
        var composed = ComposeMonitorLayout(
            snapshot.Bitmap,
            virtualBounds,
            monitors);
        return new CapturedImage(composed, virtualBounds);
    }

    internal static Bitmap ComposeMonitorLayout(
        Bitmap virtualDesktopSnapshot,
        ScreenRegion virtualBounds,
        IReadOnlyList<ScreenRegion> monitors)
    {
        ArgumentNullException.ThrowIfNull(virtualDesktopSnapshot);
        ArgumentNullException.ThrowIfNull(monitors);

        if (virtualBounds.IsEmpty)
        {
            throw new ArgumentException(
                "虚拟桌面区域不能为空。",
                nameof(virtualBounds));
        }

        if (virtualDesktopSnapshot.Width != virtualBounds.Width ||
            virtualDesktopSnapshot.Height != virtualBounds.Height)
        {
            throw new ArgumentException(
                "虚拟桌面快照尺寸与桌面区域不一致。",
                nameof(virtualDesktopSnapshot));
        }

        var visibleMonitors = monitors
            .Select(monitor => ScreenRegion.Intersect(monitor, virtualBounds))
            .Where(monitor => !monitor.IsEmpty)
            .ToArray();
        if (visibleMonitors.Length == 0)
        {
            throw new ArgumentException(
                "至少需要一个位于虚拟桌面内的显示器。",
                nameof(monitors));
        }

        var composed = new Bitmap(
            virtualBounds.Width,
            virtualBounds.Height,
            PixelFormat.Format32bppPArgb);

        try
        {
            using var graphics = Graphics.FromImage(composed);
            graphics.Clear(Color.White);
            graphics.CompositingMode = CompositingMode.SourceCopy;
            graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
            graphics.PixelOffsetMode = PixelOffsetMode.Half;

            foreach (var monitor in visibleMonitors)
            {
                var destination = new Rectangle(
                    monitor.X - virtualBounds.X,
                    monitor.Y - virtualBounds.Y,
                    monitor.Width,
                    monitor.Height);
                var source = new Rectangle(
                    destination.X,
                    destination.Y,
                    destination.Width,
                    destination.Height);
                graphics.DrawImage(
                    virtualDesktopSnapshot,
                    destination,
                    source,
                    GraphicsUnit.Pixel);
            }

            if (visibleMonitors.Length > 1)
            {
                using var dividerBrush = new SolidBrush(MonitorDividerColor);
                foreach (var monitor in visibleMonitors)
                {
                    var x = monitor.X - virtualBounds.X;
                    var y = monitor.Y - virtualBounds.Y;
                    graphics.FillRectangle(
                        dividerBrush,
                        x,
                        y,
                        monitor.Width,
                        1);
                    graphics.FillRectangle(
                        dividerBrush,
                        x,
                        y + monitor.Height - 1,
                        monitor.Width,
                        1);
                    graphics.FillRectangle(
                        dividerBrush,
                        x,
                        y,
                        1,
                        monitor.Height);
                    graphics.FillRectangle(
                        dividerBrush,
                        x + monitor.Width - 1,
                        y,
                        1,
                        monitor.Height);
                }
            }

            return composed;
        }
        catch
        {
            composed.Dispose();
            throw;
        }
    }
}
