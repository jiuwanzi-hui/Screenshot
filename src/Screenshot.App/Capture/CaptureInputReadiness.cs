using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Screenshot.App.Capture;

internal static class CaptureInputReadiness
{
    // Activation and dispatcher Render priority are not a presentation fence.
    // USER32 must see nontransparent pixels before a layered window can take
    // mouse input. Probe briefly without rendering/polling a full screen.
    internal static async Task<bool> WaitAsync(
        Func<bool> ready, Func<bool> ownsForeground, Action recoverRendering,
        CancellationToken cancellationToken, TimeSpan? timeout = null,
        TimeSpan? recoveryDelay = null, Action? recoverInput = null)
    {
        var limit = timeout ?? TimeSpan.FromSeconds(2);
        var recoveryAt = recoveryDelay ?? TimeSpan.FromMilliseconds(250);
        var clock = Stopwatch.StartNew();
        var recovered = false;
        var inputRecovered = false;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ready()) return true;
            var focused = ownsForeground();
            if (!focused && recoverInput is null) return false;
            if (clock.Elapsed >= limit) return false;
            if (!inputRecovered && recoverInput is not null)
            {
                inputRecovered = true;
                recoverInput();
            }
            if (focused && !recovered && clock.Elapsed >= recoveryAt)
            {
                recovered = true;
                recoverRendering();
            }
            await Task.Delay(25, cancellationToken);
        }
    }

    internal static bool OwnsForeground(IntPtr window) =>
        window != IntPtr.Zero && GetForegroundWindow() == window;

    internal static InvalidOperationException BlockedSurfaceFailure(IntPtr window)
    {
        var foreground = GetForegroundWindow();
        var hit = WindowFromPoint(System.Windows.Forms.Cursor.Position);
        _ = GetWindowThreadProcessId(foreground, out var foregroundProcess);
        _ = GetWindowThreadProcessId(hit, out var hitProcess);
        // Keep implementation details in the error log's inner exception,
        // separate from the user-facing Message. Never read dialog text/pixels.
        return new("截图画面尚未显示或仍被其他窗口遮挡，无法接收框选操作。",
            new InvalidOperationException($"capture=0x{window.ToInt64():X}; " +
                $"foreground=0x{foreground.ToInt64():X} pid={foregroundProcess}; " +
                $"pointerWindow=0x{hit.ToInt64():X} pid={hitProcess} exStyle=0x{GetWindowLongW(hit, -20):X8}"));
    }

    internal static bool ReceivesPointer(IntPtr window, System.Drawing.Point point)
    {
        var hit = WindowFromPoint(point);
        return hit != IntPtr.Zero && GetAncestor(hit, 2 /* GA_ROOT */) == window;
    }

    internal static bool SurfaceReceivesPointer(IntPtr window, ScreenRegion surface)
    {
        var foreground = GetForegroundWindow();
        if (foreground != IntPtr.Zero && HasForeignMouseCapture(window,
            GetWindowThreadProcessId(foreground, out _))) return false;
        foreach (var screen in System.Windows.Forms.Screen.AllScreens)
        {
            var bounds = ScreenRegion.Intersect(surface, new ScreenRegion(
                screen.Bounds.X, screen.Bounds.Y, screen.Bounds.Width, screen.Bounds.Height));
            if (!bounds.IsEmpty && !ReceivesPointer(window,
                new System.Drawing.Point(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2))) return false;
        }
        // A small topmost dialog may miss both monitor centers and the pointer.
        // Verify its overlap too before declaring the screenshot interactive.
        if (foreground != window && foreground != IntPtr.Zero && GetWindowRect(foreground, out var rect))
        {
            var bounds = ScreenRegion.Intersect(surface, new ScreenRegion(rect.Left, rect.Top,
                Math.Max(0, rect.Right - rect.Left), Math.Max(0, rect.Bottom - rect.Top)));
            if (!bounds.IsEmpty && !ReceivesPointer(window,
                new System.Drawing.Point(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2))) return false;
        }
        return ReceivesPointer(window, System.Windows.Forms.Cursor.Position);
    }

    internal static bool HasForeignMouseCapture(IntPtr window, uint foregroundThread)
    {
        var info = new GuiThreadInfo { Size = (uint)Marshal.SizeOf<GuiThreadInfo>() };
        if (foregroundThread == 0 || !GetGUIThreadInfo(foregroundThread, ref info)) return false;
        // WindowFromPoint describes Z-order, not the destination of captured
        // input. A foreground menu/drag may still own mouse routing above us.
        return info.Capture != IntPtr.Zero && GetAncestor(info.Capture, 2) != window ||
            info.MenuOwner != IntPtr.Zero && GetAncestor(info.MenuOwner, 2) != window ||
            (info.Flags & 0x1C) != 0 && GetAncestor(info.Active, 2) != window;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThreadInfo
    {
        public uint Size, Flags;
        public IntPtr Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        public int Left, Top, Right, Bottom;
    }
    [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll")] private static extern int GetWindowLongW(IntPtr window, int index);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(System.Drawing.Point point);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
}
