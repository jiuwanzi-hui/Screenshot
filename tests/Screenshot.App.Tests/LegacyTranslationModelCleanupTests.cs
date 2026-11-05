using System.IO;
using Screenshot.App.Core;
using Screenshot.App.Text;

namespace Screenshot.App.Tests;

public sealed class LegacyTranslationModelCleanupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "Screenshot.App.Tests", Guid.NewGuid().ToString("N"));
    private string Replacement => Path.Combine(_root,
        AppMetadata.LocalLargeTranslationModelDirectoryName);
    private string Legacy => Path.Combine(_root, LegacyTranslationModelCleanup.DirectoryName);

    public LegacyTranslationModelCleanupTests()
    {
        Directory.CreateDirectory(Replacement);
        Directory.CreateDirectory(Path.Combine(Legacy, "runtime"));
        File.WriteAllBytes(Path.Combine(Legacy, "model.gguf"), new byte[17]);
        File.WriteAllBytes(Path.Combine(Legacy, "runtime", "llama-cli.exe"), new byte[5]);
        File.WriteAllText(Path.Combine(Replacement, "replacement.gguf"), "new model");
    }

    [Fact]
    public async Task DeletesOnlyObsoleteSiblingAfterVerificationAndIsIdempotent()
    {
        foreach (var name in new[] { "Bergamot-Multilingual", "PP-OCRv6-Small", "OtherModel" })
        {
            Directory.CreateDirectory(Path.Combine(_root, name));
            File.WriteAllText(Path.Combine(_root, name, "keep.bin"), name);
        }
        File.WriteAllText(Path.Combine(_root, "settings.json"), "user settings");
        var validations = 0;
        Task<bool> Validate(CancellationToken _) { validations++; return Task.FromResult(true); }

        var bytes = await LegacyTranslationModelCleanup.TryCleanAsync(Replacement, Validate, default);
        Assert.Equal(22, bytes);
        Assert.False(Directory.Exists(Legacy));
        Assert.Equal("new model", File.ReadAllText(Path.Combine(Replacement, "replacement.gguf")));
        Assert.Equal("user settings", File.ReadAllText(Path.Combine(_root, "settings.json")));
        foreach (var name in new[] { "Bergamot-Multilingual", "PP-OCRv6-Small", "OtherModel" })
            Assert.Equal(name, File.ReadAllText(Path.Combine(_root, name, "keep.bin")));
        Assert.Equal(0, await LegacyTranslationModelCleanup.TryCleanAsync(Replacement, Validate, default));
        Assert.Equal(1, validations);
    }

    [Fact]
    public async Task KeepsLegacyWhenReplacementFailsIntegrityCheck()
    {
        Assert.Equal(0, await LegacyTranslationModelCleanup.TryCleanAsync(
            Replacement, _ => Task.FromResult(false), default));
        AssertLegacyIntact();
    }

    [Fact]
    public async Task LightweightReplacementCanRetireQwenWhileItIsInUse()
    {
        var lightweight = Path.Combine(_root, "Hy-MT2-1.8B");
        Directory.CreateDirectory(lightweight);
        var modelPath = Path.Combine(lightweight, "new.gguf");
        File.WriteAllText(modelPath, "verified replacement");
        using var activeModel = new FileStream(modelPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        Assert.Equal(22, await LegacyTranslationModelCleanup.TryCleanAsync(
            lightweight, _ => Task.FromResult(true), default));
        Assert.False(Directory.Exists(Legacy));
        Assert.True(File.Exists(modelPath));
    }

    [Fact]
    public async Task KeepsLegacyWhenReplacementIsNotInstalled()
    {
        using var manager = new LocalLargeTranslationModelManager(Replacement);
        Assert.Equal(0, await manager.CleanupLegacyModelAsync(default));
        AssertLegacyIntact();
    }

    [Fact]
    public async Task DoesNotCleanBesideAnArbitraryInstallationDirectory()
    {
        var custom = Path.Combine(_root, "custom");
        Directory.CreateDirectory(custom);
        Assert.Equal(0, await LegacyTranslationModelCleanup.TryCleanAsync(
            custom, _ => throw new InvalidOperationException("Must not validate"), default));
        AssertLegacyIntact();
    }

    [Fact]
    public async Task KeepsLegacyWhenCancelledAfterVerification()
    {
        using var cancellation = new CancellationTokenSource();
        Assert.Equal(0, await LegacyTranslationModelCleanup.TryCleanAsync(
            Replacement, _ => { cancellation.Cancel(); return Task.FromResult(true); }, cancellation.Token));
        AssertLegacyIntact();
    }

    [Fact]
    public async Task KeepsBusyLegacyAndRetriesAfterItIsReleased()
    {
        using (var held = new FileStream(Path.Combine(Legacy, "model.gguf"),
                   FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.Equal(0, await LegacyTranslationModelCleanup.TryCleanAsync(
                Replacement, _ => Task.FromResult(true), default));
            AssertLegacyIntact();
        }

        Assert.Equal(22, await LegacyTranslationModelCleanup.TryCleanAsync(
            Replacement, _ => Task.FromResult(true), default));
        Assert.False(Directory.Exists(Legacy));
    }

    [Fact]
    public async Task KeepsLegacyOnVerificationIoFailure()
    {
        Assert.Equal(0, await LegacyTranslationModelCleanup.TryCleanAsync(
            Replacement, _ => throw new IOException("Unavailable disk"), default));
        AssertLegacyIntact();
    }

    [Fact]
    public async Task RechecksOldFilesAfterValidation()
    {
        FileStream? held = null;
        try
        {
            Assert.Equal(0, await LegacyTranslationModelCleanup.TryCleanAsync(
                Replacement, _ =>
                {
                    held = new FileStream(Path.Combine(Legacy, "model.gguf"),
                        FileMode.Open, FileAccess.Read, FileShare.Read);
                    return Task.FromResult(true);
                }, default));
            AssertLegacyIntact();
        }
        finally { held?.Dispose(); }
    }

    private void AssertLegacyIntact()
    {
        Assert.Equal(17, new FileInfo(Path.Combine(Legacy, "model.gguf")).Length);
        Assert.Equal(5, new FileInfo(Path.Combine(Legacy, "runtime", "llama-cli.exe")).Length);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
