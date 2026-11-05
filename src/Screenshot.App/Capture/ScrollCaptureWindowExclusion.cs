using System.Runtime.InteropServices;

namespace Screenshot.App.Capture;

internal static class ScrollCaptureWindowExclusion
{
    internal static bool Exclude(IntPtr handle) =>
        handle != IntPtr.Zero && SetWindowDisplayAffinity(handle, 0x00000011);

    // Set this once when locking the selection. These HWNDs live until the
    // session closes; changing affinity around each copy makes the compositor
    // repeatedly rebuild the full-screen mask and can visibly flash it.
    internal static bool ExcludeForSession(IReadOnlyList<IntPtr> handles)
    {
        var excluded = true;
        foreach (var handle in handles) excluded &= Exclude(handle);
        return excluded;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(IntPtr handle, uint affinity);
}
