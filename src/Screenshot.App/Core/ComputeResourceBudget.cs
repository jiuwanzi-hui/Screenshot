using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Screenshot.App.Core;

internal readonly record struct ComputeResources(int LogicalCores, int PhysicalCores, int PerformanceCores,
    int NominalMHz, bool HasAvx2, long AvailableRamBytes, double CpuBusyFraction);

// On-demand only, on a computation thread. No WMI, startup benchmark or resident polling.
internal static class ComputeResourceBudget
{
    private const long GiB = 1024L * 1024 * 1024;

    internal static ComputeResources Capture() => Capture(null);

    internal static ComputeResources Capture(ComputeResources? topology)
    {
        var logical = Environment.ProcessorCount;
        var physical = topology?.PhysicalCores ?? 0;
        var performance = topology?.PerformanceCores ?? 0;
        var mhz = topology?.NominalMHz ?? 0;
        if (topology is null) try
        {
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            _ = GetSystemCpuSetInformation(IntPtr.Zero, 0, out var size, process.Handle, 0);
            if (size is > 0 and <= 1024 * 1024)
            {
                var buffer = Marshal.AllocHGlobal((int)size);
                try
                {
                    if (GetSystemCpuSetInformation(buffer, size, out var returned, process.Handle, 0))
                    {
                        var cores = new Dictionary<(short Group, byte Core), byte>();
                        for (var offset = 0; offset + 20 <= Math.Min(size, returned);)
                        {
                            var item = IntPtr.Add(buffer, offset);
                            var length = Marshal.ReadInt32(item);
                            if (length < 20 || offset + (long)length > Math.Min(size, returned)) break;
                            if (Marshal.ReadInt32(item, 4) == 0)
                            {
                                var flags = Marshal.ReadByte(item, 19);
                                // CPU sets assigned exclusively to another process are unavailable.
                                if ((flags & 2) == 0 || (flags & 4) != 0)
                                    cores[(Marshal.ReadInt16(item, 12), Marshal.ReadByte(item, 15))] = Marshal.ReadByte(item, 18);
                            }
                            offset += length;
                        }
                        if (cores.Count > 0)
                        {
                            physical = Math.Min(logical, cores.Count);
                            var fastest = cores.Values.Max();
                            performance = Math.Min(physical, cores.Values.Count(value => value == fastest));
                        }
                    }
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            mhz = key?.GetValue("~MHz") is int frequency ? frequency : 0;
        }
        catch (Exception exception) when (exception is EntryPointNotFoundException or System.Security.SecurityException or
            UnauthorizedAccessException or System.ComponentModel.Win32Exception or System.IO.IOException) { }

        var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        var ram = GlobalMemoryStatusEx(ref memory) ? (long)Math.Min(memory.AvailablePhysical, (ulong)long.MaxValue) : 0;
        var busy = -1d;
        if (GetSystemTimes(out var idle, out var kernel, out var user))
        {
            // One short sample per native worker start, never on the UI/input thread.
            Thread.Sleep(64);
            if (GetSystemTimes(out var nextIdle, out var nextKernel, out var nextUser))
                busy = BusyFraction(idle, kernel + user, nextIdle, nextKernel + nextUser);
        }
        return new(logical, physical, performance, mhz,
            System.Runtime.Intrinsics.X86.Avx2.IsSupported, ram, busy);
    }

    internal static double BusyFraction(ulong idle, ulong total, ulong nextIdle, ulong nextTotal) =>
        nextTotal <= total || nextIdle < idle ? -1 :
        Math.Clamp(1 - (nextIdle - idle) / (double)(nextTotal - total), 0, 1);

    internal static int OcrThreads(ComputeResources resources)
    {
        if (resources.PhysicalCores <= 0) return Limit(HeavyWorkloadBudget.CalculateOcrThreadCount(resources.LogicalCores), resources);
        var capacity = resources.PerformanceCores + (resources.PhysicalCores - resources.PerformanceCores) * .5;
        // Same-page Small and Medium measurements improve through 12 threads;
        // 16 brings little gain. Keep the same pool for preparation/inference.
        var desired = capacity switch { >= 16 => 12, >= 12 => 10, >= 8 => 6, >= 6 => 4, >= 4 => 3, _ => 1 };
        // Four physical cores without SMT otherwise lose half their capacity.
        // Same-image four-core runs: 3 threads ~1.1s vs 2 ~1.5s, identical text.
        // Keep one core free; pressure and RAM limits below still apply.
        var reserve = resources.LogicalCores == 4 && resources.PhysicalCores == 4 &&
            resources.PerformanceCores == 4 && resources.HasAvx2 && resources.AvailableRamBytes >= 4 * GiB ? 1 : 2;
        return Limit(desired, resources, reserve);
    }

    internal static int TranslationThreads(ComputeResources resources, bool gpu, bool batch)
    {
        var physical = resources.PhysicalCores > 0 ? resources.PhysicalCores : Math.Max(1, resources.LogicalCores / 2);
        var capacity = resources.PerformanceCores > 0 ? resources.PerformanceCores : physical;
        // CPU token generation is bandwidth bound; prefill can also use efficiency
        // cores. GPU execution only needs a modest host pool to submit kernels.
        var desired = gpu ? Math.Clamp(capacity / 2, 2, 6) : batch ? physical - 2 : capacity - 1;
        return Limit(desired, resources);
    }

    internal static int OcrRecognitionParallelism(int threads, long availableRamBytes)
    {
        // Same-image runs kept the identical model, resolution, text and boxes.
        // Concurrent rows share one ONNX thread pool; don't multiply that pool.
        if (threads >= 10 && availableRamBytes >= 8 * GiB) return 3;
        if (threads >= 4 && availableRamBytes >= 4 * GiB) return 2;
        return 1;
    }

    private static int Limit(int desired, ComputeResources resources, int reservedLogicalCores = 2)
    {
        var budget = Math.Min(desired, Math.Max(1, resources.LogicalCores - reservedLogicalCores));
        if (resources.NominalMHz is > 0 and < 1800 || !resources.HasAvx2) budget = Math.Min(budget, 4);
        if (resources.AvailableRamBytes is > 0 and < 2 * GiB) budget = Math.Min(budget, 2);
        else if (resources.AvailableRamBytes is > 0 and < 4 * GiB) budget = Math.Min(budget, 4);
        if (resources.CpuBusyFraction >= .5)
            budget = Math.Min(budget, (int)Math.Floor(resources.LogicalCores * (1 - resources.CpuBusyFraction)) - 1);
        return Math.Max(1, budget);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length, Load;
        public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile,
            TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemCpuSetInformation(IntPtr information, uint length, out uint returnedLength, IntPtr process, uint flags);
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);
}
