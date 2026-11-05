using System.Diagnostics;
using System.IO;
using Screenshot.App.Core;
using Screenshot.App.Text;
using Xunit.Abstractions;

namespace Screenshot.App.Tests;

public sealed class LocalTranslationHardwareCacheTests(ITestOutputHelper output)
{
    private static TranslationHardwareProfile Profile => new("machine-driver-runtime",
        new ComputeResources(16, 8, 8, 2895, true, 16L * 1024 * 1024 * 1024, .1),
        new("Vulkan0", 1) { IsDiscrete = true, Name = "NVIDIA GeForce GTX 1650 Ti", FreeMemoryMiB = 3600 }, null, null);

    [Fact]
    public async Task SurvivesNewInstanceWithoutReusingOldFreeMemoryAndInvalidatesChangedHardware()
    {
        var root = Path.Combine(Path.GetTempPath(), "SnapCut-hardware-cache-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(root, "hardware.json");
            await new LocalTranslationHardwareCache(path).SaveAsync(Profile, CancellationToken.None);
            var next = await new LocalTranslationHardwareCache(path).ReadAsync(Profile.Fingerprint, CancellationToken.None);
            Assert.NotNull(next);
            Assert.Equal(8, next.Cpu.PhysicalCores);
            Assert.Equal(0, next.Cpu.AvailableRamBytes);
            Assert.Equal(0, next.Device!.FreeMemoryMiB);
            Assert.True(next.Device.IsDiscrete);
            Assert.Equal(Profile.Device!.Name, next.Device.Name);
            Assert.Null(await new LocalTranslationHardwareCache(path).ReadAsync("another-driver", CancellationToken.None));
            await File.WriteAllTextAsync(path, "broken cache");
            Assert.Null(await new LocalTranslationHardwareCache(path).ReadAsync(Profile.Fingerprint, CancellationToken.None));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void FailedProbeHasShortExpiryAndDoesNotBecomePermanentNoGpuDetection()
    {
        var now = DateTimeOffset.UtcNow;
        var failed = Profile with { Device = null, ProbeFailure = "timeout", RetryAfter = now.AddMinutes(10) };
        Assert.True(LocalTranslationHardwareCache.IsCurrent(failed, failed.Fingerprint, now));
        Assert.False(LocalTranslationHardwareCache.IsCurrent(failed, failed.Fingerprint, now.AddMinutes(11)));
        Assert.True(LocalTranslationHardwareCache.IsCurrent(Profile, Profile.Fingerprint, now.AddYears(1)));
    }

    [Theory]
    [InlineData(3600, true)]
    [InlineData(1500, false)]
    public void FourGiBGpuRetainsFreshBudgetAndDoesNotReuseHeadroom(long budget, bool direct)
    {
        var refreshed = LocalTranslationInferenceSession.RefreshCachedDevice(Profile.Device,
            [new(Profile.Device!.Name, "adapter", budget)])!;
        Assert.Equal(budget, refreshed.FreeMemoryMiB);
        Assert.Equal(budget >= 3000 ? 2 : 1, refreshed.Parallelism);
        Assert.Equal(direct, LocalTranslationStartupPolicy.Select(refreshed, 1_133_080_448, 16L * 1024 * 1024 * 1024, true).DirectOffload);
        Assert.Equal(0, LocalTranslationInferenceSession.RefreshCachedDevice(Profile.Device, [])!.FreeMemoryMiB);
    }

    [Fact]
    public void NativeBudgetReadReturnsStableIdentityWithoutLoadingVulkan()
    {
        var watch = Stopwatch.StartNew();
        var first = WindowsInferenceHardware.Capture();
        var firstMs = watch.Elapsed.TotalMilliseconds;
        watch.Restart();
        var second = WindowsInferenceHardware.Capture();
        output.WriteLine($"WDDM budget/identity: first={firstMs:F1} ms; repeat={watch.Elapsed.TotalMilliseconds:F1} ms; adapters={string.Join("; ", second.Adapters)}");
        Assert.Equal(first.Fingerprint, second.Fingerprint);
        Assert.Equal(64, first.Fingerprint.Length);
        Assert.All(first.Adapters, adapter => Assert.InRange(adapter.BudgetMiB, 0, 1024 * 1024));
    }
}
