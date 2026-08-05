namespace Screenshot.App.Core;

internal static class HeavyWorkloadBudget
{
    private const int MaximumCpuThreads = 4;

    internal static int CpuThreadCount =>
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
}
