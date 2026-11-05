using System.Globalization;
using System.Runtime.InteropServices;

namespace Screenshot.App.Capture;

// One inexpensive native snapshot per diagnostic event, never a polling loop.
// Cumulative CPU/fault counters distinguish compute from waits and page-ins.
internal static class CaptureDiagnosticSnapshot
{
    internal static string Read()
    {
        var process = new IntPtr(-1);
        var memory = new MemoryCounters { Size = (uint)Marshal.SizeOf<MemoryCounters>() };
        var cpu = GetProcessTimes(process, out _, out _, out var kernel, out var user)
            ? ((kernel + user) / 10000d).ToString("0.###", CultureInfo.InvariantCulture) : "unavailable";
        var hasMemory = GetProcessMemoryInfo(process, ref memory, memory.Size);
        var foreground = GetForegroundWindow();
        _ = GetWindowThreadProcessId(foreground, out var foregroundProcess);
        return $" cpuMs={cpu} faults={(hasMemory ? memory.PageFaultCount.ToString(CultureInfo.InvariantCulture) : "unavailable")}" +
            $" wsBytes={(hasMemory ? memory.WorkingSetSize.ToUInt64().ToString(CultureInfo.InvariantCulture) : "unavailable")}" +
            $" privateBytes={(hasMemory ? memory.PrivateUsage.ToUInt64().ToString(CultureInfo.InvariantCulture) : "unavailable")}" +
            $" gen2={GC.CollectionCount(2)} poolPending={ThreadPool.PendingWorkItemCount}" +
            $" foreground={foreground.ToInt64()} foregroundPid={foregroundProcess} foregroundVisible={IsWindowVisible(foreground)}";
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryCounters
    {
        public uint Size, PageFaultCount;
        public UIntPtr PeakWorkingSetSize, WorkingSetSize, QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage,
            QuotaPeakNonPagedPoolUsage, QuotaNonPagedPoolUsage, PagefileUsage, PeakPagefileUsage, PrivateUsage;
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(IntPtr process, out long created, out long exited, out long kernel, out long user);
    [DllImport("psapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessMemoryInfo(IntPtr process, ref MemoryCounters counters, uint size);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
}
