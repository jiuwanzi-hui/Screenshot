using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Screenshot.App.Core;
using Screenshot.App.Infrastructure;
using Screenshot.App.Update;

namespace Screenshot.App.Tests;

public sealed class RollbackSettingsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "SnapCut.RollbackTests", Guid.NewGuid().ToString("N"));
    private string SettingsPath => Path.Combine(_root, "ScreenshotData", "settings.json");
    private static readonly JsonSerializerOptions OldOptions = new()
    { PropertyNameCaseInsensitive = true, Converters = { new JsonStringEnumConverter() } };

    // Enum layout shipped in 4.1.0; intentionally has no AspectRatio member.
    public enum OldToolbarFeature
    {
        Shape, Arrow, Emoji, Number, Brush, Text, Mosaic, VideoRecording, Save,
        ScrollCapture, TextRecognition, CopyTable, CopyRecognizedText, Translation,
        PrivacyRedaction, PinImage, UndoRedo, Line, DoubleArrow, Watermark,
    }
    public sealed record OldSettings
    {
        public string RegionCaptureHotKey { get; init; } = "default";
        public string SaveDirectory { get; init; } = "default";
        public OldToolbarFeature[] VisibleCaptureToolbarFeatures { get; init; } = [];
        public Dictionary<OldToolbarFeature, string> CaptureToolbarFeatureShortcuts { get; init; } = [];
    }

    [Fact]
    public void NewToolbarEnumDoesNotResetUnrelatedPreferencesInOldReader()
    {
        var store = new SettingsStore(SettingsPath);
        store.Save(AppSettings.CreateDefault() with
        {
            RegionCaptureHotKey = "Alt+A", Theme = AppTheme.GlassInk,
            SaveDirectory = Path.Combine(_root, "my pictures"),
            CaptureToolbarFeatureShortcuts = new() { [CaptureToolbarFeature.Shape] = "1", [CaptureToolbarFeature.AspectRatio] = "Ctrl+9" },
        });
        var original = File.ReadAllBytes(SettingsPath);
        // Reproduce the former complete-load failure using the old converter.
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<OldSettings>(original, OldOptions));
        var snapshot = RollbackSettingsSnapshot.Create(_root, new(4, 1, 3, 0), new(4, 1, 0));
        Assert.NotNull(snapshot);
        Assert.Equal(original, File.ReadAllBytes(SettingsPath)); // Snapshot creation itself must not mutate settings.
        snapshot.Apply();
        var old = JsonSerializer.Deserialize<OldSettings>(File.ReadAllText(SettingsPath), OldOptions)!;
        Assert.Equal("Alt+A", old.RegionCaptureHotKey);
        Assert.Equal(Path.Combine(_root, "my pictures"), old.SaveDirectory);
        Assert.Contains(OldToolbarFeature.Translation, old.VisibleCaptureToolbarFeatures);
        Assert.Equal("1", old.CaptureToolbarFeatureShortcuts[OldToolbarFeature.Shape]);
        var current = new SettingsStore(SettingsPath).Load();
        Assert.Null(current.Warning);
        Assert.Equal(AppTheme.GlassInk, current.Settings.Theme);
        Assert.Equal("Ctrl+9", current.Settings.CaptureToolbarFeatureShortcuts[CaptureToolbarFeature.AspectRatio]);
        var backups = Directory.GetFiles(Path.Combine(_root, "ScreenshotData", "VersionBackups"), "settings.json", SearchOption.AllDirectories);
        Assert.Equal(original, File.ReadAllBytes(Assert.Single(backups)));
    }

    [Fact]
    public void SnapshotPreservesUnknownFieldsAndUserTextThatLooksLikeAnEnum()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, """{"theme":"GlassInk","watermark":{"text":"AspectRatio"},"future":{"mode":"AspectRatio"}}""");
        RollbackSettingsSnapshot.Create(_root, new(4, 1, 3), new(4, 1, 2))!.Apply();
        using var json = JsonDocument.Parse(File.ReadAllText(SettingsPath));
        Assert.Equal((int)AppTheme.GlassInk, json.RootElement.GetProperty("theme").GetInt32());
        Assert.Equal("AspectRatio", json.RootElement.GetProperty("watermark").GetProperty("text").GetString());
        Assert.Equal("AspectRatio", json.RootElement.GetProperty("future").GetProperty("mode").GetString());
    }

    [Theory]
    [InlineData("4.1.3")]
    [InlineData("4.1.4")]
    public void ReinstallAndUpgradeDoNotRewriteSettings(string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, "original");
        Assert.Null(RollbackSettingsSnapshot.Create(_root, new(4, 1, 3, 0), Version.Parse(target)));
        Assert.Equal("original", File.ReadAllText(SettingsPath));
    }

    [Fact]
    public void UnreadableConfigurationStopsRollbackWithoutReplacingIt()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        const string original = "{ incomplete configuration";
        File.WriteAllText(SettingsPath, original);
        Assert.ThrowsAny<JsonException>(() => RollbackSettingsSnapshot.Create(_root, new(4, 1, 3), new(4, 1, 0)));
        Assert.Equal(original, File.ReadAllText(SettingsPath));
    }

    [Fact]
    public void FailedConfigurationWriteKeepsOriginalAndItsBackup()
    {
        new SettingsStore(SettingsPath).Save(AppSettings.CreateDefault() with { RegionCaptureHotKey = "Alt+A" });
        var original = File.ReadAllBytes(SettingsPath);
        var snapshot = RollbackSettingsSnapshot.Create(_root, new(4, 1, 3), new(4, 1, 0))!;
        using (var locked = File.Open(SettingsPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.True(Record.Exception(() => snapshot.Apply()) is IOException or UnauthorizedAccessException);
        Assert.Equal(original, File.ReadAllBytes(SettingsPath));
        Assert.Equal(original, File.ReadAllBytes(Assert.Single(Directory.GetFiles(
            Path.Combine(_root, "ScreenshotData", "VersionBackups"), "settings.json", SearchOption.AllDirectories))));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(SettingsPath)!, "*.rollback-*"));
    }

    [Fact]
    public void StagedReplacementCannotOverwriteCredentialsHistoryOrDownloadedModels()
    {
        var staging = Path.Combine(_root, "staging");
        var target = Path.Combine(_root, "app");
        var protectedFiles = new[] { "ScreenshotData/settings.json", "ScreenshotData/translation-credentials.dat",
            "ScreenshotData/Watermarks/logo.png", "ScreenshotData/HistoryCache/history.png",
            "ScreenshotData/Updates/download.7z", "TranslationModels/PP-OCRv6-Small/model.onnx",
            "TranslationModels/TranslateGemma-4B-Q4-K-M/model.gguf", "TranslationModels/en-zh/model.bin" };
        foreach (var relative in protectedFiles.Append("SnapCut.exe"))
            foreach (var (directory, content) in new[] { (staging, "replacement"), (target, "personal") })
            {
                var path = Path.Combine(directory, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, content);
            }
        PortableUpdateRunner.CommitStagedFiles(staging, target);
        Assert.Equal("replacement", File.ReadAllText(Path.Combine(target, "SnapCut.exe")));
        foreach (var relative in protectedFiles)
            Assert.Equal("personal", File.ReadAllText(Path.Combine(target, relative)));
    }

    [Fact]
    public async Task CurrentFrameworkDependentRunnerStartsWithoutAnApplicationOrOldPackageExecutable()
    {
        var runner = Path.Combine(_root, "helper", ApplicationUpdateLauncher.RunnerFileName);
        ApplicationUpdateLauncher.StageRunner(Path.Combine(AppContext.BaseDirectory, "SnapCut.exe"), runner, frameworkDependent: true);
        var start = new ProcessStartInfo(runner) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add(PortableUpdateRunner.UpdateArgument); // Invalid arguments stop before writes or restart.
        using var process = Process.Start(start)!;
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(2, process.ExitCode);
            Assert.False(Directory.Exists(Path.Combine(_root, "helper", "ScreenshotData")));
        }
        finally { if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(); } }
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
}
