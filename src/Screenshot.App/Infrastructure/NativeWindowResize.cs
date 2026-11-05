using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Screenshot.App.Infrastructure;

internal static class NativeWindowResize
{
    internal static int HitTestFor(string direction) => direction switch
    {
        "Left" => 10, "Right" => 11, "Top" => 12, "TopLeft" => 13,
        "TopRight" => 14, "Bottom" => 15, "BottomLeft" => 16, "BottomRight" => 17,
        _ => throw new ArgumentOutOfRangeException(nameof(direction)),
    };

    internal static void Begin(Window window, string direction)
    {
        var hitTest = HitTestFor(direction);
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero || !window.IsVisible) return;
        window.Dispatcher.VerifyAccess();
        System.Windows.Input.Mouse.Capture(null);
        _ = ReleaseCapture();
        var pointer = System.Windows.Forms.Cursor.Position;
        var position = new IntPtr(unchecked((pointer.Y << 16) | (pointer.X & 0xffff)));
        // Enter the same native move/size loop used by WindowChrome. Preserve
        // the actual grab point; SC_SIZE can warp the pointer to the edge.
        // No application timer or accumulated mouse deltas run in this loop.
        _ = SendMessage(handle, 0x00A1, new IntPtr(hitTest), position);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReleaseCapture();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr handle, int message, IntPtr wParam, IntPtr lParam);
}
