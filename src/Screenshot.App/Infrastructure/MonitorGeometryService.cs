using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using DrawingRectangle = System.Drawing.Rectangle;
using WinForms = System.Windows.Forms;

namespace Screenshot.App.Infrastructure;

internal readonly record struct MonitorDpiScale(double X, double Y);

internal static class MonitorGeometryService
{
    private const uint MonitorDefaultToNearest = 0x00000002;
    private const uint SetWindowPositionNoSize = 0x0001;
    private const uint SetWindowPositionNoZOrder = 0x0004;
    private const uint SetWindowPositionNoActivate = 0x0010;
    private const int EffectiveDpi = 0;

    public static DrawingRectangle GetWorkArea(DrawingRectangle referenceBounds)
    {
        return WinForms.Screen.FromRectangle(referenceBounds).WorkingArea;
    }

    public static DrawingRectangle GetWorkArea(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        var handle = new WindowInteropHelper(window).Handle;
        return handle == IntPtr.Zero
            ? WinForms.Screen.PrimaryScreen?.WorkingArea ??
              WinForms.SystemInformation.VirtualScreen
            : WinForms.Screen.FromHandle(handle).WorkingArea;
    }

    public static MonitorDpiScale GetDpiScale(DrawingRectangle referenceBounds)
    {
        var nativeBounds = new NativeRect(
            referenceBounds.Left,
            referenceBounds.Top,
            referenceBounds.Right,
            referenceBounds.Bottom);
        try
        {
            var monitor = MonitorFromRect(
                ref nativeBounds,
                MonitorDefaultToNearest);
            if (monitor != IntPtr.Zero &&
                GetDpiForMonitor(
                    monitor,
                    EffectiveDpi,
                    out var dpiX,
                    out var dpiY) == 0 &&
                dpiX > 0 && dpiY > 0)
            {
                return new MonitorDpiScale(dpiX / 96d, dpiY / 96d);
            }
        }
        catch (Exception exception) when (
            exception is DllNotFoundException or EntryPointNotFoundException)
        {
        }

        return new MonitorDpiScale(1, 1);
    }

    public static bool TryGetWindowBounds(
        Window window,
        out DrawingRectangle bounds)
    {
        ArgumentNullException.ThrowIfNull(window);
        var handle = new WindowInteropHelper(window).Handle;
        if (handle != IntPtr.Zero && GetWindowRect(handle, out var nativeBounds))
        {
            bounds = DrawingRectangle.FromLTRB(
                nativeBounds.Left,
                nativeBounds.Top,
                nativeBounds.Right,
                nativeBounds.Bottom);
            return bounds.Width > 0 && bounds.Height > 0;
        }

        bounds = DrawingRectangle.Empty;
        return false;
    }

    public static bool TryMoveWindow(Window window, int x, int y)
    {
        ArgumentNullException.ThrowIfNull(window);
        var handle = new WindowInteropHelper(window).Handle;
        return handle != IntPtr.Zero && SetWindowPos(
            handle,
            IntPtr.Zero,
            x,
            y,
            0,
            0,
            SetWindowPositionNoSize |
            SetWindowPositionNoZOrder |
            SetWindowPositionNoActivate);
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct NativeRect(
        int Left,
        int Top,
        int Right,
        int Bottom);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromRect(
        ref NativeRect rectangle,
        uint flags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(
        IntPtr monitor,
        int dpiType,
        out uint dpiX,
        out uint dpiY);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(
        IntPtr window,
        out NativeRect rectangle);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr window,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);
}
