using System.Runtime;
using System.Runtime.InteropServices;

namespace Screenshot.App.Core;

/// <summary>
/// Hands the memory retired by a finished capture session back to the OS.
/// </summary>
/// <remarks>
/// A capture session allocates tens of megabytes of short-lived bitmaps and
/// pixel buffers. The GC reclaims them eventually, but it neither compacts
/// the large-object heap nor returns the freed pages promptly, so a tray
/// application that should idle small kept sitting at hundreds of megabytes
/// of working set. Trimming is only requested at session boundaries — never
/// on a hot path — so the cost of re-faulting pages is paid where the user
/// cannot feel it.
/// </remarks>
internal static class MemoryFootprint
{
    public static void TrimAfterHeavyOperation()
    {
        try
        {
            GCSettings.LargeObjectHeapCompactionMode =
                GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            _ = NativeMethods.EmptyWorkingSet(
                NativeMethods.GetCurrentProcess());
        }
        catch (Exception)
        {
            // Trimming is best-effort and must never break the flow it ends.
        }
    }

    private static class NativeMethods
    {
        [DllImport("kernel32.dll")]
        public static extern IntPtr GetCurrentProcess();

        [DllImport("psapi.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool EmptyWorkingSet(IntPtr process);
    }
}
