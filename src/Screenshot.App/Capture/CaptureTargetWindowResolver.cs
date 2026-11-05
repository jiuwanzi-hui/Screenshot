using System.Runtime.InteropServices;

namespace Screenshot.App.Capture;

// Resolve through SnapCut's visible selection without hiding any HWND. A
// hidden/re-shown owner also hides its preview and produces several flashes.
internal static class CaptureTargetWindowResolver
{
    private const string ContentWindowProperty = "SnapCut.CaptureContentWindow";

    // Settings is valid capture content even though it shares the overlay's
    // process. The property belongs to the HWND and dies with the window.
    internal static void AllowContentWindow(IntPtr window) => SetProp(window, ContentWindowProperty, new IntPtr(1));

    private static bool IsCaptureChrome(IntPtr window, uint process) =>
        process == (uint)Environment.ProcessId && GetProp(GetAncestor(window, 2), ContentWindowProperty) == IntPtr.Zero;

    internal static IntPtr FindUnderlyingWindow(ScreenRegion region)
    {
        var point = new Point { X = region.X + region.Width / 2, Y = region.Y + region.Height / 2 };
        var hit = WindowFromPoint(point);
        _ = GetWindowThreadProcessId(hit, out var hitProcess);
        if (hit != IntPtr.Zero && !IsCaptureChrome(hit, hitProcess)) return hit;

        var window = GetTopWindow(IntPtr.Zero);
        for (var count = 0; window != IntPtr.Zero && count < 4096; count++, window = GetWindow(window, 2))
        {
            _ = GetWindowThreadProcessId(window, out var process);
            if (IsCaptureChrome(window, process) || !IsWindowVisible(window) ||
                !GetWindowRect(window, out var bounds) || point.X < bounds.Left || point.X >= bounds.Right ||
                point.Y < bounds.Top || point.Y >= bounds.Bottom) continue;
            if (DwmGetWindowAttribute(window, 14, out var cloaked, sizeof(int)) == 0 && cloaked != 0) continue;

            for (var depth = 0; depth < 64; depth++)
            {
                var clientPoint = point;
                if (!ScreenToClient(window, ref clientPoint)) break;
                var child = ChildWindowFromPointEx(window, clientPoint, 1 | 2 | 4);
                if (child == IntPtr.Zero || child == window) break;
                window = child;
            }
            return window;
        }
        return IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X; public int Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left; public int Top; public int Right; public int Bottom; }
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool SetProp(IntPtr window, string name, IntPtr value);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetProp(IntPtr window, string name);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetTopWindow(IntPtr parent);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window, uint command);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(IntPtr window, out Rect bounds);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool ScreenToClient(IntPtr window, ref Point point);
    [DllImport("user32.dll")] private static extern IntPtr ChildWindowFromPointEx(IntPtr parent, Point point, uint flags);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr window, uint attribute, out int value, int size);
}
