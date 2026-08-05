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
            VideoSaveDirectory = Path.Combine(_testDirectory, "videos"),
            RecordSystemAudio = false,
            RecordMicrophone = true,
            VideoRecordingCodec = VideoRecordingCodec.H265,
            VideoRecordingFrameRate = 60,
            VideoRecordingHotKey = "Ctrl+Alt+R",
            ShowKeyboardInputInRecording = true,
            ShowMouseInputInRecording = true,
            ShowTaskbarIcon = true,
            ShowFloatingCaptureButton = true,
            FloatingCaptureClickBehavior =
                FloatingCaptureClickBehavior.CaptureAllScreens,
            CloseBehavior = WindowCloseBehavior.ExitApplication,
            MouseLongPressMilliseconds = 1150,
            MouseSideButtonsUseLongPress = true,
            OcrEngine = OcrEngineMode.PaddleOcrV6,
            OfflineTranslationQuality = OfflineTranslationQuality.Ultra,
            OfflineTranslationEngine = OfflineTranslationEngine.QwenLargeModel,
        };

        store.Save(settings);
        var loadResult = store.Load();

        Assert.Null(loadResult.Warning);
        Assert.True(loadResult.Settings.LaunchAtStartup);
        Assert.True(loadResult.Settings.ShowTaskbarIcon);
        Assert.True(loadResult.Settings.ShowFloatingCaptureButton);
        Assert.Equal(
            FloatingCaptureClickBehavior.CaptureAllScreens,
            loadResult.Settings.FloatingCaptureClickBehavior);
        Assert.Equal(settings.CloseBehavior, loadResult.Settings.CloseBehavior);
        Assert.Equal(1150, loadResult.Settings.MouseLongPressMilliseconds);
        Assert.True(loadResult.Settings.MouseSideButtonsUseLongPress);
        Assert.Equal(OcrEngineMode.PaddleOcrV6, loadResult.Settings.OcrEngine);
        Assert.Equal(Path.GetFullPath(settings.SaveDirectory), loadResult.Settings.SaveDirectory);
        Assert.Equal(
            Path.GetFullPath(settings.VideoSaveDirectory),
            loadResult.Settings.VideoSaveDirectory);
        Assert.False(loadResult.Settings.RecordSystemAudio);
        Assert.True(loadResult.Settings.RecordMicrophone);
        Assert.Equal(
            VideoRecordingCodec.H265,
            loadResult.Settings.VideoRecordingCodec);
        Assert.Equal(60, loadResult.Settings.VideoRecordingFrameRate);
        Assert.Equal("Ctrl+Alt+R", loadResult.Settings.VideoRecordingHotKey);
        Assert.True(loadResult.Settings.ShowKeyboardInputInRecording);
        Assert.True(loadResult.Settings.ShowMouseInputInRecording);
        Assert.Equal(
            OfflineTranslationQuality.Ultra,
            loadResult.Settings.OfflineTranslationQuality);
        Assert.Equal(
            OfflineTranslationEngine.QwenLargeModel,
            loadResult.Settings.OfflineTranslationEngine);
    }

    [Fact]
    public void UsesDefaultsWhenTheSettingsFileIsInvalid()
    {
        File.WriteAllText(_settingsPath, "{ invalid json");

        var loadResult = new SettingsStore(_settingsPath).Load();

        Assert.NotNull(loadResult.Warning);
        Assert.Equal(AppSettings.CreateDefault().RegionCaptureHotKey, loadResult.Settings.RegionCaptureHotKey);
    }

    [Fact]
    public void MigratesLegacyThemeAndDropsAccentColorWhenSaved()
    {
        File.WriteAllText(
            _settingsPath,
            """
            {
              "settingsVersion": 3,
              "theme": "Dark",
              "accentColor": "#8A5BD6"
            }
            """);
        var store = new SettingsStore(_settingsPath);

        var loaded = store.Load();

        Assert.Null(loaded.Warning);
        Assert.Equal(AppTheme.ForestNight, loaded.Settings.Theme);
        store.Save(loaded.Settings);
        var savedJson = File.ReadAllText(_settingsPath);
        Assert.DoesNotContain("accentColor", savedJson, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ForestNight", savedJson, StringComparison.Ordinal);
    }

    [Fact]
    public void NewAndLegacySettingsDefaultToVisibleTaskbarAndNotificationIcons()
    {
        var defaults = AppSettings.CreateDefault();
        Assert.True(defaults.ShowTaskbarIcon);
        Assert.True(defaults.ShowNotificationIcon);
        Assert.False(defaults.ShowFloatingCaptureButton);
        Assert.True(defaults.RecordSystemAudio);
        Assert.False(defaults.RecordMicrophone);
        Assert.Equal(VideoRecordingCodec.H264, defaults.VideoRecordingCodec);
        Assert.Equal(30, defaults.VideoRecordingFrameRate);
        Assert.Equal(
            FloatingCaptureClickBehavior.ShowSelection,
            defaults.FloatingCaptureClickBehavior);

        File.WriteAllText(_settingsPath, "{ \"SettingsVersion\": 1 }");
        var loadResult = new SettingsStore(_settingsPath).Load();

        Assert.Null(loadResult.Warning);
        Assert.True(loadResult.Settings.ShowTaskbarIcon);
        Assert.True(loadResult.Settings.ShowNotificationIcon);
        Assert.False(loadResult.Settings.ShowFloatingCaptureButton);
        Assert.True(loadResult.Settings.RecordSystemAudio);
        Assert.False(loadResult.Settings.RecordMicrophone);
        Assert.Equal(VideoRecordingCodec.H264, loadResult.Settings.VideoRecordingCodec);
        Assert.Equal(30, loadResult.Settings.VideoRecordingFrameRate);
        Assert.Equal(
            FloatingCaptureClickBehavior.ShowSelection,
            loadResult.Settings.FloatingCaptureClickBehavior);
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
