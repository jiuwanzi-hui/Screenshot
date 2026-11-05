using Screenshot.App.Core;
using Screenshot.App.Text;

namespace Screenshot.App.Tests;

public sealed class ComputeResourceBudgetTests
{
    [Theory]
    [InlineData(2, 16, 1)]
    [InlineData(6, 2, 1)]
    [InlineData(6, 4, 2)]
    [InlineData(12, 6, 2)]
    [InlineData(12, 8, 3)]
    [InlineData(12, 0, 1)]
    public void RecognitionRowsShareThreadsButRespectRamHeadroom(int threads, int ramGiB, int expected) =>
        Assert.Equal(expected, ComputeResourceBudget.OcrRecognitionParallelism(threads, ramGiB * 1024L * 1024 * 1024));
    private const long GiB = 1024L * 1024 * 1024;
    private static ComputeResources Desktop => new(24, 24, 8, 3700, true, 32 * GiB, .1);

    [Fact]
    public void SameLogicalCountDoesNotImplySameOcrBudget()
    {
        var desktop = ComputeResourceBudget.OcrThreads(Desktop);
        var smt = ComputeResourceBudget.OcrThreads(Desktop with { PhysicalCores = 12, PerformanceCores = 12 });
        var slow = ComputeResourceBudget.OcrThreads(Desktop with { NominalMHz = 1500 });
        Assert.Equal(12, desktop);
        Assert.InRange(smt, 5, desktop - 1);
        Assert.InRange(slow, 1, smt - 1);
    }

    [Theory]
    [InlineData(2, 2, 1)]
    [InlineData(4, 2, 1)]
    [InlineData(4, 4, 3)]
    [InlineData(8, 4, 3)]
    [InlineData(16, 8, 6)]
    public void PhysicalCoreBudgetsLeaveCapacityForDesktop(int logical, int physical, int expected)
    {
        var resources = Desktop with { LogicalCores = logical, PhysicalCores = physical, PerformanceCores = physical };
        Assert.Equal(expected, ComputeResourceBudget.OcrThreads(resources));
        Assert.InRange(ComputeResourceBudget.TranslationThreads(resources, false, true), 1, Math.Max(1, logical - 2));
    }

    [Fact]
    public void BusyMachineAndLowMemoryReduceComputeWithoutChangingTheModel()
    {
        Assert.InRange(ComputeResourceBudget.OcrThreads(Desktop with { CpuBusyFraction = .9 }), 1, 2);
        Assert.InRange(ComputeResourceBudget.OcrThreads(Desktop with { AvailableRamBytes = GiB }), 1, 2);
        Assert.InRange(ComputeResourceBudget.OcrThreads(Desktop with { HasAvx2 = false }), 1, 4);
        Assert.True(ComputeResourceBudget.TranslationThreads(Desktop, false, true) > 4);
        Assert.True(ComputeResourceBudget.TranslationThreads(Desktop, false, false) > 4);
        Assert.InRange(ComputeResourceBudget.TranslationThreads(Desktop, true, false), 2, 6);
    }

    [Fact]
    public void UnknownMeasurementsAreNotZeroResources()
    {
        var unknown = Desktop with { PhysicalCores = 0, PerformanceCores = 0, NominalMHz = 0,
            AvailableRamBytes = 0, CpuBusyFraction = -1 };
        Assert.InRange(ComputeResourceBudget.OcrThreads(unknown), 2, 12);
        Assert.Equal(-1, ComputeResourceBudget.BusyFraction(1, 2, 1, 2));
        Assert.Equal(.75, ComputeResourceBudget.BusyFraction(100, 200, 125, 300));
    }

    [Fact]
    public void GpuSlotsRespectModelHeadroomWorkSizeAndSystemPressure()
    {
        var gpu = new LocalTranslationInferenceSession.InferenceDevice("Vulkan0", 8)
            { IsDiscrete = true, FreeMemoryMiB = 15000 };
        Assert.Equal(8, LocalTranslationStartupPolicy.Parallelism(gpu, 2_489_909_312, Desktop, 60));
        Assert.Equal(1, LocalTranslationStartupPolicy.Parallelism(gpu, 2_489_909_312, Desktop, 1));
        Assert.Equal(1, LocalTranslationStartupPolicy.Parallelism(gpu with { FreeMemoryMiB = 3500 }, 2_489_909_312, Desktop, 60));
        Assert.Equal(2, LocalTranslationStartupPolicy.Parallelism(gpu, 2_489_909_312, Desktop with { CpuBusyFraction = .9 }, 60));
        Assert.Equal(2, LocalTranslationStartupPolicy.Parallelism(gpu, 2_489_909_312, Desktop with { AvailableRamBytes = 3 * GiB }, 60));
        Assert.Equal(1, LocalTranslationStartupPolicy.Parallelism(gpu with { IsDiscrete = false }, GiB, Desktop, 60));
        Assert.Equal(2, LocalTranslationStartupPolicy.Parallelism(gpu with { FreeMemoryMiB = 3500 }, 1_133_080_448, Desktop, 60));
        Assert.Equal(1, LocalTranslationStartupPolicy.Parallelism(gpu with { FreeMemoryMiB = 1800 }, 1_133_080_448, Desktop, 60));
    }

    [Fact]
    public async Task WindowsProbeReportsPhysicalTopologyAndBoundedSample()
    {
        var resources = await Task.Run(ComputeResourceBudget.Capture);
        Assert.InRange(resources.PhysicalCores, 1, resources.LogicalCores);
        Assert.InRange(resources.PerformanceCores, 1, resources.PhysicalCores);
        Assert.True(resources.AvailableRamBytes > 0);
        Assert.InRange(resources.CpuBusyFraction, 0, 1);
    }

    [Theory]
    [InlineData(3500, 2, true)]
    [InlineData(3000, 2, true)]
    [InlineData(2700, 2, true)]
    [InlineData(2300, 1, true)]
    [InlineData(1800, 1, false)]
    public void LightweightModelUsesMeasuredBuffersWithoutSerializingHealthyFourGiBGpu(long budget, int slots, bool direct)
    {
        var gpu = LocalTranslationInferenceSession.RefreshCachedDevice(
            new("Vulkan0", 2) { IsDiscrete = true, Name = "NVIDIA GeForce GTX 1650 Ti" },
            [new("NVIDIA GeForce GTX 1650 Ti", "adapter", budget)])!;
        var count = LocalTranslationStartupPolicy.Parallelism(gpu, 1_133_080_448, Desktop, 12, lightweight: true);
        Assert.Equal(slots, count);
        Assert.Equal(direct, LocalTranslationStartupPolicy.Select(gpu with { Parallelism = count }, 1_133_080_448, 16 * GiB, true).DirectOffload);
        Assert.Equal(1, LocalTranslationStartupPolicy.Parallelism(gpu, 1_133_080_448, Desktop, 1, lightweight: true));
    }
}
