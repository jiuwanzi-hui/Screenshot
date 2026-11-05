using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using Screenshot.App.Text;

namespace Screenshot.App.Tests;

public sealed class LocalInferenceRuntimeTests
{
    [LocalRuntimeTheory]
    [InlineData(true)]
    public async Task PinnedManifestMatchesAuthenticatedPackageAndFastPathStillRepairsCorruption(bool lightweight)
    {
        using var manager = new LocalLargeTranslationModelManager(
            Environment.GetEnvironmentVariable("SNAPCUT_TRANSLATION_MODEL_DIRECTORY"), lightweight: lightweight);
        var archivePath = Path.Combine(manager.InstallationDirectory, "llama-b10276-bin-win-vulkan-x64.zip");
        Assert.Equal(LocalInferenceRuntimeManifest.ArchiveHash,
            Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(archivePath))));
        using var archive = ZipFile.OpenRead(archivePath);
        foreach (var entry in archive.Entries.Where(entry => LocalInferenceRuntime.Include(entry.Name, false)))
        {
            Assert.True(LocalInferenceRuntimeManifest.TryGetHash(LocalInferenceRuntimeManifest.ArchiveHash, entry.Name, out var hash));
            await using var stream = entry.Open();
            Assert.Equal(hash, Convert.ToHexString(await SHA256.HashDataAsync(stream)));
            Assert.False(LocalInferenceRuntimeManifest.TryGetHash("different archive", entry.Name, out _));
        }
        var root = Path.Combine(Path.GetTempPath(), "SnapCut-manifest-" + Guid.NewGuid().ToString("N"));
        try
        {
            foreach (var cpuOnly in new[] { true, false })
            {
                var destination = Path.Combine(root, cpuOnly ? "cpu" : "vulkan");
                await LocalInferenceRuntime.PrepareAsync(archivePath, LocalInferenceRuntimeManifest.ArchiveHash, destination, cpuOnly, CancellationToken.None);
                var library = Path.Combine(destination, "llama.dll");
                await using (var damage = new FileStream(library, FileMode.Open, FileAccess.Write))
                    damage.WriteByte(0);
                await LocalInferenceRuntime.PrepareAsync(archivePath, LocalInferenceRuntimeManifest.ArchiveHash, destination, cpuOnly, CancellationToken.None);
                Assert.True(LocalInferenceRuntimeManifest.TryGetHash(LocalInferenceRuntimeManifest.ArchiveHash, "llama.dll", out var expected));
                Assert.Equal(expected, Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(library))));
                Assert.Equal(!cpuOnly, File.Exists(Path.Combine(destination, "ggml-vulkan.dll")));
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task VerifiedPackageRepairsSameSizeCorruptionAndSeparatesBackends()
    {
        var root = Path.Combine(Path.GetTempPath(), "SnapCut-runtime-layout-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "runtime.zip");
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
                foreach (var name in new[] { "llama-server.exe", "ggml-cpu-x64.dll", "ggml-vulkan.dll", "ggml-rpc.dll", "llama-bench.exe" })
                {
                    using var writer = new StreamWriter(zip.CreateEntry("nested/" + name).Open());
                    writer.Write(name);
                }
            var hash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path)));
            var cpu = Path.Combine(root, "cpu");
            var gpu = Path.Combine(root, "gpu");
            await LocalInferenceRuntime.PrepareAsync(path, hash, cpu, true, CancellationToken.None);
            Assert.False(File.Exists(Path.Combine(cpu, "ggml-vulkan.dll")));
            Assert.False(File.Exists(Path.Combine(cpu, "ggml-rpc.dll")));
            Assert.False(File.Exists(Path.Combine(cpu, "llama-bench.exe")));
            await File.WriteAllTextAsync(Path.Combine(cpu, "ggml-cpu-x64.dll"), new string('x', "ggml-cpu-x64.dll".Length));
            await LocalInferenceRuntime.PrepareAsync(path, hash, cpu, true, CancellationToken.None);
            Assert.Equal("ggml-cpu-x64.dll", await File.ReadAllTextAsync(Path.Combine(cpu, "ggml-cpu-x64.dll")));
            await LocalInferenceRuntime.PrepareAsync(path, hash, gpu, false, CancellationToken.None);
            Assert.True(File.Exists(Path.Combine(gpu, "ggml-vulkan.dll")));
            await Assert.ThrowsAsync<InvalidDataException>(() => LocalInferenceRuntime.PrepareAsync(path, "bad-hash", cpu, true, CancellationToken.None));
            await File.WriteAllTextAsync(Path.Combine(cpu, "ggml-vulkan.dll"), "unexpected backend");
            await Assert.ThrowsAsync<InvalidDataException>(() => LocalInferenceRuntime.PrepareAsync(path, hash, cpu, true, CancellationToken.None));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void IsolatedEnvironmentIsScopedToChildAndRetainsExplicitLayerSettings()
    {
        var before = Environment.GetEnvironmentVariable("GGML_BACKEND_PATH");
        var start = new ProcessStartInfo();
        start.Environment["GGML_BACKEND_PATH"] = "foreign-gpu.dll";
        start.Environment["VK_LOADER_LAYERS_DISABLE"] = "existing-filter";
        start.Environment["VK_INSTANCE_LAYERS"] = "explicit-layer";
        LocalInferenceRuntime.ConfigureEnvironment(start, true);
        Assert.False(start.Environment.ContainsKey("GGML_BACKEND_PATH"));
        Assert.Equal("existing-filter,~implicit~", start.Environment["VK_LOADER_LAYERS_DISABLE"]);
        Assert.Equal("explicit-layer", start.Environment["VK_INSTANCE_LAYERS"]);
        Assert.Equal(before, Environment.GetEnvironmentVariable("GGML_BACKEND_PATH"));
    }
}
