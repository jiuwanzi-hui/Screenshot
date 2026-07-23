using System.Runtime.InteropServices;

namespace Screenshot.App.Capture;

public static class VirtualScreen
{
    private const int VirtualScreenLeftMetric = 76;
    private const int VirtualScreenTopMetric = 77;
    private const int VirtualScreenWidthMetric = 78;
    private const int VirtualScreenHeightMetric = 79;

    public static ScreenRegion GetBounds()
    {
        var region = new ScreenRegion(
            NativeMethods.GetSystemMetrics(VirtualScreenLeftMetric),
            NativeMethods.GetSystemMetrics(VirtualScreenTopMetric),
            NativeMethods.GetSystemMetrics(VirtualScreenWidthMetric),
            NativeMethods.GetSystemMetrics(VirtualScreenHeightMetric));

        if (region.IsEmpty)
        {
            throw new InvalidOperationException("无法读取当前虚拟桌面尺寸。");
        }

        return region;
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll")]
        public static extern int GetSystemMetrics(int systemMetric);
    }
}
