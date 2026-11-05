using System.Runtime.InteropServices;
using Screenshot.App.Core;

namespace Screenshot.App.Text;

internal readonly record struct LocalTranslationStartupOptions(bool DirectOffload, bool BufferedLoading);

internal static class LocalTranslationStartupPolicy
{
    private const long MiB = 1024L * 1024;

    internal static int Parallelism(LocalTranslationInferenceSession.InferenceDevice device, long modelBytes,
        ComputeResources resources, int workItems, bool lightweight = false)
    {
        if (!device.IsDiscrete || modelBytes <= 0 || workItems <= 1) return 1;
        // Budget weights, desktop/work buffers and a full 4096-token context per
        // slot. Constrained devices retain native fitting and a single request.
        // The pinned Hy-MT2 Q4_K_M / b10276 worker measured 1076 MiB weights,
        // 256 MiB KV per 4096-token slot and 24 MiB GPU compute buffers. Reserve
        // 768 MiB plus 128 MiB per slot beyond those allocations. Reusing the
        // larger model's doubled-weight budget needlessly serialized 4 GB GPUs.
        var remaining = lightweight
            ? device.FreeMemoryMiB * MiB - modelBytes - 768 * MiB
            : device.FreeMemoryMiB * MiB - modelBytes * 3 / 2 - 1024 * MiB;
        var memorySlots = (int)Math.Clamp(remaining / (384 * MiB), 1, 8);
        var slots = Math.Min(device.Parallelism, Math.Min(memorySlots, workItems));
        if (resources.AvailableRamBytes is > 0 and < 4096 * MiB) slots = Math.Min(slots, 2);
        if (resources.CpuBusyFraction >= .8) slots = Math.Min(slots, 2);
        // 4/8/12/16 measured on the same dense page: above 8 gave no meaningful
        // throughput gain, while allocating larger context buffers.
        return Math.Max(1, slots);
    }

    internal static LocalTranslationStartupOptions Select(
        LocalTranslationInferenceSession.InferenceDevice? device, long modelBytes, long availableRamBytes,
        bool lightweight = false)
    {
        if (device is not { IsDiscrete: true } || modelBytes <= 0) return default;
        // Keep substantial room for context, working buffers, the desktop and
        // other applications. Marginal/shared-memory devices retain native fit.
        var requiredVram = lightweight
            ? modelBytes + (768 + device.Parallelism * 384) * MiB
            : Math.Max(8192 * MiB, modelBytes * 2 + device.Parallelism * 256 * MiB);
        var directOffload = device.FreeMemoryMiB * MiB >= requiredVram;
        // Buffered loading is faster on the measured discrete-GPU path, but
        // its temporary host allocation must not force a small PC to page out.
        var bufferedLoading = directOffload && availableRamBytes >= Math.Max(8192 * MiB, modelBytes * 2 + 2048 * MiB);
        return new(directOffload, bufferedLoading);
    }

    internal static long AvailableRamBytes()
    {
        var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        return GlobalMemoryStatusEx(ref status) ? (long)Math.Min(status.AvailablePhysical, (ulong)long.MaxValue) : 0;
    }

    internal static string? ResourceFailure(long modelBytes, long availableRamBytes, bool cpuOnly)
    {
        if (availableRamBytes <= 0) return null; // A failed probe is not zero memory.
        var required = cpuOnly ? modelBytes + 1024 * MiB : 512 * MiB;
        return availableRamBytes >= required ? null :
            $"当前可用内存约 {availableRamBytes / (1024d * MiB):F1} GB，" +
            $"{(cpuOnly ? "CPU 运行此模型" : "加载离线模型")}建议至少预留约 {required / (1024d * MiB):F1} GB。" +
            "为避免长时间换页等待，已停止本次本机加载；请关闭占内存的软件，或在设置 → 翻译选择在线翻译／轻量模型。";
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
}
