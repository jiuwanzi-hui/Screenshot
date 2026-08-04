using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
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

        var bitmap = new Bitmap(region.Width, region.Height, PixelFormat.Format32bppPArgb);

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
