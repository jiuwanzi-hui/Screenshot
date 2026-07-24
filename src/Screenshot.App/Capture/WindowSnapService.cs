using System.Runtime.InteropServices;

namespace Screenshot.App.Capture;

public static class WindowSnapService
{
    private const int DwmExtendedFrameBounds = 9;
    private const int DwmCloaked = 14;

    public static bool TryGetWindowRegionAt(
        int screenX,
        int screenY,
        IntPtr excludedWindow,
        ScreenRegion virtualScreen,
        out ScreenRegion region)
    {
        region = default;
        var selectedRegion = default(ScreenRegion);
        var found = false;
        _ = NativeMethods.EnumWindows((windowHandle, _) =>
        {
            if (windowHandle == excludedWindow ||
                !NativeMethods.IsWindowVisible(windowHandle) ||
                NativeMethods.IsIconic(windowHandle) ||
                IsCloaked(windowHandle) ||
                IsDesktopWindow(windowHandle) ||
                !TryGetWindowBounds(windowHandle, out var candidate) ||
                candidate.Width < 40 ||
                candidate.Height < 30 ||
                !candidate.Contains(screenX, screenY))
            {
                return true;
            }

            candidate = ScreenRegion.Intersect(candidate, virtualScreen);
            if (candidate.IsEmpty)
            {
                return true;
            }

            selectedRegion = candidate;
            found = true;
            return false;
        }, IntPtr.Zero);
        region = selectedRegion;
        return found;
    }

    private static bool TryGetWindowBounds(
        IntPtr windowHandle,
        out ScreenRegion region)
    {
        NativeRectangle rectangle;
        if (NativeMethods.DwmGetWindowAttribute(
                windowHandle,
                DwmExtendedFrameBounds,
                out rectangle,
                Marshal.SizeOf<NativeRectangle>()) != 0 &&
            !NativeMethods.GetWindowRect(windowHandle, out rectangle))
        {
            region = default;
            return false;
        }

        region = ScreenRegion.FromCorners(
            rectangle.Left,
            rectangle.Top,
            rectangle.Right,
            rectangle.Bottom);
        return !region.IsEmpty;
    }

    private static bool IsCloaked(IntPtr windowHandle)
    {
        return NativeMethods.DwmGetWindowAttribute(
                   windowHandle,
                   DwmCloaked,
                   out int cloaked,
                   sizeof(int)) == 0 &&
               cloaked != 0;
    }

    private static bool IsDesktopWindow(IntPtr windowHandle)
    {
        var className = new char[128];
        var length = NativeMethods.GetClassName(
            windowHandle,
            className,
            className.Length);
        var value = length > 0
            ? new string(className, 0, length)
            : string.Empty;
        return value is "Progman" or "WorkerW";
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRectangle
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private static class NativeMethods
    {
        public delegate bool EnumWindowsCallback(
            IntPtr windowHandle,
            IntPtr parameter);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool EnumWindows(
            EnumWindowsCallback callback,
            IntPtr parameter);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsWindowVisible(IntPtr windowHandle);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsIconic(IntPtr windowHandle);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetWindowRect(
            IntPtr windowHandle,
            out NativeRectangle rectangle);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetClassName(
            IntPtr windowHandle,
            [Out] char[] className,
            int maximumCount);

        [DllImport("dwmapi.dll")]
        public static extern int DwmGetWindowAttribute(
            IntPtr windowHandle,
            int attribute,
            out NativeRectangle value,
            int valueSize);

        [DllImport("dwmapi.dll")]
        public static extern int DwmGetWindowAttribute(
            IntPtr windowHandle,
            int attribute,
            out int value,
            int valueSize);
    }
}
