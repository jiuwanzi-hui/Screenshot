using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Screenshot.App.Capture;

// Call only after the original desktop pixels have been captured. In
// particular F1 does not dismiss a classic menu the way an Alt chord can.
internal static class CaptureInputActivation
{
    internal enum Result { Focused, PointerReady, Interrupted, Failed }

    internal static Task<Result> AcquireAsync(IntPtr window, Func<bool> pointerReady, CancellationToken token) =>
        AcquireAsync(new NativeActivation(window, pointerReady), token);

    internal static async Task<Result> AcquireAsync(IActivation activation, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var foreground = activation.Foreground;
        // TOPMOST is a band, not a guarantee that we are above another topmost
        // dialog. Reorder our surface after its pixels exist, even if focused.
        activation.RaiseSurface();
        if (activation.OwnsForeground) return Result.Focused;
        var menu = activation.GetForegroundMenu();

        // Post only to the foreground menu's owner/capture HWND. Never send
        // global Esc, cancel an unrelated drag, or synchronously wait on the
        // other application's menu thread.
        if (menu is not null) activation.CancelMenu(menu.Value);
        var clock = Stopwatch.StartNew();
        var attempts = 0;
        do
        {
            token.ThrowIfCancellationRequested();
            if (activation.OwnsForeground) return Result.Focused;
            if (!activation.IsSameForeground(foreground)) return Result.Interrupted;
            if (activation.GetForegroundMenu() is null)
            {
                // Windows activation can finish on a later message turn. Give
                // it a bounded retry without attaching to another app's queue.
                if (attempts == 0 || attempts == 1 && clock.Elapsed >= TimeSpan.FromMilliseconds(100))
                {
                    if (attempts++ != 0) activation.RaiseSurface();
                    if (activation.TryActivate()) return Result.Focused;
                }
                // A visible full-screen input surface can accept the first
                // click even when foreground activation is temporarily denied.
                if (!activation.IsSameForeground(foreground)) return Result.Interrupted;
                if (activation.PointerReady) return Result.PointerReady;
            }
            await Task.Delay(16, token);
        } while (clock.Elapsed < TimeSpan.FromMilliseconds(350));
        return activation.OwnsForeground ? Result.Focused : Result.Failed;
    }

    internal readonly record struct Menu(IntPtr Foreground, IntPtr Owner, IntPtr Capture);

    internal interface IActivation
    {
        bool OwnsForeground { get; }
        IntPtr Foreground { get; }
        bool PointerReady { get; }
        void RaiseSurface();
        Menu? GetForegroundMenu();
        void CancelMenu(Menu menu);
        bool IsSameForeground(IntPtr foreground);
        bool TryActivate();
    }

    private sealed class NativeActivation(IntPtr window, Func<bool> pointerReady) : IActivation
    {
        public bool OwnsForeground => CaptureInputReadiness.OwnsForeground(window);
        public IntPtr Foreground => GetForegroundWindow();
        public bool PointerReady => pointerReady();
        public void RaiseSurface() => CaptureInputActivation.RaiseSurface(window);
        public bool IsSameForeground(IntPtr foreground) => GetForegroundWindow() is var current &&
            (current == foreground || current == IntPtr.Zero);
        public bool TryActivate()
        {
            if (window == IntPtr.Zero || !IsWindow(window)) return false;
            _ = SetForegroundWindow(window);
            if (!OwnsForeground) return false;
            _ = SetFocus(window);
            return true;
        }

        public Menu? GetForegroundMenu()
        {
            var foreground = GetForegroundWindow();
            if (foreground == IntPtr.Zero || foreground == window) return null;
            var thread = GetWindowThreadProcessId(foreground, out _);
            var info = new GuiThreadInfo { Size = (uint)Marshal.SizeOf<GuiThreadInfo>() };
            if (!GetGUIThreadInfo(thread, ref info) ||
                (info.MenuOwner == IntPtr.Zero && (info.Flags & 0x1C) == 0)) return null;
            return new(foreground, info.MenuOwner != IntPtr.Zero ? info.MenuOwner : foreground, info.Capture);
        }

        public void CancelMenu(Menu menu)
        {
            CaptureTimingDiagnostics.Mark("capture-menu-cancel");
            _ = PostMessageW(menu.Owner, 0x001F /* WM_CANCELMODE */, IntPtr.Zero, IntPtr.Zero);
            if (menu.Capture != IntPtr.Zero && menu.Capture != menu.Owner)
                _ = PostMessageW(menu.Capture, 0x001F, IntPtr.Zero, IntPtr.Zero);
        }
    }

    internal static void RaiseSurface(IntPtr window)
    {
        if (window == IntPtr.Zero || !IsWindow(window)) return;
        _ = GetWindowThreadProcessId(window, out var process);
        if (process != (uint)Environment.ProcessId) return;
        // Move only our own HWND. Do not demote the dialog, modify the taskbar,
        // join foreign input queues or synthesize Alt/Esc to obtain permission.
        _ = SetWindowPos(window, new IntPtr(-1), 0, 0, 0, 0,
            0x0001 | 0x0002 | 0x0010 | 0x0200); // NOSIZE | NOMOVE | NOACTIVATE | NOOWNERZORDER
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThreadInfo
    {
        public uint Size, Flags;
        public IntPtr Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr SetFocus(IntPtr window);
    [DllImport("user32.dll")] private static extern bool PostMessageW(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}
