using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Screenshot.App.Infrastructure;

// A screen-sized capture/annotation surface must not trigger Explorer's
// fullscreen heuristic, which lowers the taskbar below unrelated windows.
// Set before Show/resize, not after capture or only when the surface closes.
internal static class OverlayShellBehavior
{
    internal const string NonFullscreenProperty = "NonRudeHWND";

    internal static void Apply(IntPtr window)
    {
        _ = GetWindowThreadProcessId(window, out var processId);
        if (window == IntPtr.Zero || processId != (uint)Environment.ProcessId)
            throw new ArgumentException("Only an owned overlay window may be marked.", nameof(window));

        // Documented by ITaskbarList2::MarkFullscreenWindow. Passing FALSE to
        // that COM method alone would still permit automatic fullscreen detection.
        // This property changes only our HWND; no taskbar/app Z-order is modified.
        if (!SetProp(window, NonFullscreenProperty, new IntPtr(1)))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    [DllImport("user32.dll", EntryPoint = "SetPropW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProp(IntPtr window, string name, IntPtr value);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
}
