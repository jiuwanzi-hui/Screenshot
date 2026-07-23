using System.IO;
using Screenshot.App.Core;
using Screenshot.App.Infrastructure;

namespace Screenshot.App.Tests;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _testDirectory;
    private readonly string _settingsPath;

    public SettingsStoreTests()
    {
        _testDirectory = Path.Combine(
            Path.GetTempPath(),
            "Screenshot.App.Tests",
            Guid.NewGuid().ToString("N"));
        _settingsPath = Path.Combine(_testDirectory, "settings.json");
        Directory.CreateDirectory(_testDirectory);
    }

    [Fact]
    public void SavesAndLoadsSettings()
    {
        var store = new SettingsStore(_settingsPath);
        var settings = AppSettings.CreateDefault() with
        {
            LaunchAtStartup = true,
            SaveDirectory = Path.Combine(_testDirectory, "captures"),
            ShowTaskbarIcon = true,
            CloseBehavior = WindowCloseBehavior.ExitApplication,
        };

        store.Save(settings);
        var loadResult = store.Load();

        Assert.Null(loadResult.Warning);
        Assert.True(loadResult.Settings.LaunchAtStartup);
        Assert.True(loadResult.Settings.ShowTaskbarIcon);
        Assert.Equal(settings.CloseBehavior, loadResult.Settings.CloseBehavior);
        Assert.Equal(Path.GetFullPath(settings.SaveDirectory), loadResult.Settings.SaveDirectory);
    }

    [Fact]
    public void UsesDefaultsWhenTheSettingsFileIsInvalid()
    {
        File.WriteAllText(_settingsPath, "{ invalid json");

        var loadResult = new SettingsStore(_settingsPath).Load();

        Assert.NotNull(loadResult.Warning);
        Assert.Equal(AppSettings.CreateDefault().RegionCaptureHotKey, loadResult.Settings.RegionCaptureHotKey);
    }

    public void Dispose()
    {
        if (File.Exists(_settingsPath))
        {
            File.Delete(_settingsPath);
        }

        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory);
        }
    }
}
