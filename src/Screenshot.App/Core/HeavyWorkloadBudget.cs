namespace Screenshot.App.Core;

internal static class HeavyWorkloadBudget
{
    private const int MaximumCpuThreads = 4;
    // Evaluated only inside the short-lived OCR worker's computation thread.
    private static readonly Lazy<int> NativeOcrThreads = new(() => ComputeResourceBudget.OcrThreads(ComputeResourceBudget.Capture()));

    internal static int CpuThreadCount =>
        CalculateCpuThreadCount(Environment.ProcessorCount);

    /// <summary>
    /// Foreground OCR scales with CPU capacity. Large machines can use more
    /// native workers; smaller machines retain headroom for input/rendering.
    /// </summary>
    internal static int OcrThreadCount =>
        NativeOcrThreads.Value;

    /// <summary>
    /// Thread budget for a user-visible blocking operation such as
    /// translating a capture: the user is actively waiting, so borrow nearly
    /// every core for the duration and let the engine release them when the
    /// call completes. Background-friendly work keeps using
    /// <see cref="CpuThreadCount"/>.
    /// </summary>
    internal static int BurstCpuThreadCount =>
        CalculateBurstCpuThreadCount(Environment.ProcessorCount);

    /// <summary>Bounded budget for native translation worker pools.</summary>
    internal static int TranslationCpuThreadCount =>
        CalculateCpuThreadCount(Environment.ProcessorCount);

    internal static int CalculateCpuThreadCount(int logicalProcessorCount)
    {
        if (logicalProcessorCount <= 2)
        {
            return 1;
        }

        // Keep model quality unchanged while leaving most CPU capacity available
        // for input, rendering, recording, and other foreground applications.
        return Math.Min(
            logicalProcessorCount - 1,
            Math.Clamp(logicalProcessorCount / 4, 2, MaximumCpuThreads));
    }

    internal static int CalculateBurstCpuThreadCount(int logicalProcessorCount)
    {
        // Leave two logical cores for the UI thread and the compositor so the
        // waiting animation stays responsive while the model saturates the rest.
        return Math.Max(1, logicalProcessorCount - 2);
    }

    internal static int CalculateOcrThreadCount(int logicalProcessorCount)
    {
        if (logicalProcessorCount <= 2)
        {
            return 1;
        }

        // Conservative fallback when physical topology cannot be read. Normal
        // operation uses ComputeResourceBudget and the Medium measurements.
        return logicalProcessorCount switch
        {
            >= 32 => 12,
            >= 24 => 8,
            >= 16 => 6,
            >= 12 => 4,
            _ => 2,
        };
    }
}
