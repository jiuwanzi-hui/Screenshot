using Screenshot.App.Text;

namespace Screenshot.App.Tests;

public sealed class LocalTranslationStartupTests
{
    private const long GiB = 1024L * 1024 * 1024;

    [Theory]
    [InlineData(0, true, false)]
    [InlineData(256, false, true)]
    [InlineData(1024, false, false)]
    [InlineData(2048, true, true)]
    [InlineData(4096, true, false)]
    public void MemoryShortageExplainsAvailableMemoryAndRecoveryWithoutBlockingFailedProbes(
        long availableMiB, bool cpuOnly, bool blocked)
    {
        var error = LocalTranslationStartupPolicy.ResourceFailure(2 * GiB, availableMiB * 1024 * 1024, cpuOnly);
        Assert.Equal(blocked, error is not null);
        if (blocked)
        {
            Assert.Contains("可用内存", error);
            Assert.Contains("已停止", error);
            Assert.Contains("设置 → 翻译", error);
        }
    }

    [Theory]
    [InlineData(true, 15000, 40, true, true)]
    [InlineData(true, 9000, 8, true, true)]
    [InlineData(true, 9000, 4, true, false)]
    [InlineData(true, 7000, 40, false, false)]
    [InlineData(true, 3500, 40, false, false)]
    [InlineData(false, 32000, 40, false, false)]
    [InlineData(true, 15000, 0, true, false)]
    public void FastLoadingRequiresDedicatedMemoryAndHostMemoryHeadroom(
        bool discrete, long freeMiB, long ramGiB, bool direct, bool buffered)
    {
        var device = new LocalTranslationInferenceSession.InferenceDevice("Vulkan0", 8)
            { IsDiscrete = discrete, FreeMemoryMiB = freeMiB };
        var result = LocalTranslationStartupPolicy.Select(device, 2_489_909_312, ramGiB * GiB);
        Assert.Equal(direct, result.DirectOffload);
        Assert.Equal(buffered, result.BufferedLoading);
    }

    [Fact]
    public void LightweightModelUsesFourGiBGpuWithRoomForDesktopAndFallsBackToFittingWhenBusy()
    {
        var laptop = new LocalTranslationInferenceSession.InferenceDevice("Vulkan0", 1)
            { Name = "NVIDIA GeForce GTX 1650 Ti", IsDiscrete = true, FreeMemoryMiB = 3100 };
        Assert.True(LocalTranslationStartupPolicy.Select(laptop, 1_133_080_448, 16 * GiB,
            lightweight: true).DirectOffload);
        Assert.False(LocalTranslationStartupPolicy.Select(laptop with { FreeMemoryMiB = 1800 },
            1_133_080_448, 16 * GiB, lightweight: true).DirectOffload);
        Assert.False(LocalTranslationStartupPolicy.Select(laptop with { IsDiscrete = false },
            1_133_080_448, 16 * GiB, lightweight: true).DirectOffload);
    }

    [Fact]
    public void UnknownDevicesAndLargerModelsKeepAutomaticFitting()
    {
        Assert.Equal(default, LocalTranslationStartupPolicy.Select(null, 2 * GiB, 40 * GiB));
        var device = new LocalTranslationInferenceSession.InferenceDevice("Vulkan0", 8)
            { IsDiscrete = true, FreeMemoryMiB = 15000 };
        Assert.Equal(default, LocalTranslationStartupPolicy.Select(device, 8 * GiB, 40 * GiB));
        Assert.Equal(default, LocalTranslationStartupPolicy.Select(device, 0, 40 * GiB));
    }
}
