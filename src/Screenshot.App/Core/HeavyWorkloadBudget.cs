namespace Screenshot.App.Core;

internal static class HeavyWorkloadBudget
{
    private const int MaximumCpuThreads = 4;

    internal static int CpuThreadCount =>
        CalculateCpuThreadCount(Environment.ProcessorCount);

    /// <summary>
    /// Bounded foreground budget for a user-requested OCR operation. Four
    /// native ONNX workers are enough to accelerate small screenshot crops,
    /// while keeping input and compositor threads responsive on low-end CPUs.
    /// </summary>
    internal static int OcrThreadCount =>
        CalculateOcrThreadCount(Environment.ProcessorCount);

    /// <summary>
    /// Thread budget for a user-visible blocking operation such as
    /// translating a capture: the user is actively waiting, so borrow nearly
    /// every core for the duration and let the engine release them when the
    /// call completes. Background-friendly work keeps using
    /// <see cref="CpuThreadCount"/>.
    /// </summary>
    internal static int BurstCpuThreadCount =>
        CalculateBurstCpuThreadCount(Environment.ProcessorCount);

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

        return Math.Min(4, Math.Max(2, logicalProcessorCount / 3));
    }
}
