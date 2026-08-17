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
            CaptureToolbarPositionXRatio = 0.2,
            CaptureToolbarPositionYRatio = 0.75,
            VideoRecordingHotKey = "Ctrl+Alt+R",
            TextTranslationHotKey = "Ctrl+Alt+T",
            ShowKeyboardInputInRecording = true,
            ShowMouseInputInRecording = true,
            ShowTaskbarIcon = true,
            ShowFloatingCaptureButton = true,
            FloatingCaptureClickBehavior =
                FloatingCaptureClickBehavior.CaptureAllScreens,
            ScrollCaptureMode = ScrollCaptureMode.ManualWheel,
            ArrowStyle = ArrowStyle.Hollow,
            CustomStrokeColor = "#123456",
            CustomColorPalette = [0x123456, 0xABCDEF],
            CloseBehavior = WindowCloseBehavior.ExitApplication,
            MouseLongPressMilliseconds = 1150,
            MouseSideButtonsUseLongPress = true,
            KeepHistory = true,
            PersistHistoryAcrossRestarts = true,
            OcrEngine = OcrEngineMode.PaddleOcrV6,
            OfflineTranslationQuality = OfflineTranslationQuality.Ultra,
            OfflineTranslationEngine = OfflineTranslationEngine.QwenLargeModel,
            VisibleCaptureToolbarFeatures =
            [
                CaptureToolbarFeature.Text,
                CaptureToolbarFeature.Save,
            ],
            CaptureToolbarFeatureOrder =
            [
                CaptureToolbarFeature.Save,
                CaptureToolbarFeature.Text,
            ],
            CaptureToolbarRows = CaptureToolbarRowCount.Two,
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
        Assert.Equal(
            ScrollCaptureMode.ManualWheel,
            loadResult.Settings.ScrollCaptureMode);
        Assert.Equal(ArrowStyle.Hollow, loadResult.Settings.ArrowStyle);
        Assert.Equal("#123456", loadResult.Settings.CustomStrokeColor);
        Assert.Equal(
            [0x123456, 0xABCDEF],
            loadResult.Settings.CustomColorPalette);
        Assert.Equal(settings.CloseBehavior, loadResult.Settings.CloseBehavior);
        Assert.Equal(1150, loadResult.Settings.MouseLongPressMilliseconds);
        Assert.True(loadResult.Settings.MouseSideButtonsUseLongPress);
        Assert.True(loadResult.Settings.KeepHistory);
        Assert.True(loadResult.Settings.PersistHistoryAcrossRestarts);
        Assert.Equal(
            AppSettings.MaximumHistoryItems,
            loadResult.Settings.HistoryLimit);
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
        Assert.Equal(0.2, loadResult.Settings.CaptureToolbarPositionXRatio);
        Assert.Equal(0.75, loadResult.Settings.CaptureToolbarPositionYRatio);
        Assert.Equal("Ctrl+Alt+R", loadResult.Settings.VideoRecordingHotKey);
        Assert.Equal("Ctrl+Alt+T", loadResult.Settings.TextTranslationHotKey);
        Assert.True(loadResult.Settings.ShowKeyboardInputInRecording);
        Assert.True(loadResult.Settings.ShowMouseInputInRecording);
        Assert.Equal(
            OfflineTranslationQuality.Ultra,
            loadResult.Settings.OfflineTranslationQuality);
        Assert.Equal(
            OfflineTranslationEngine.QwenLargeModel,
            loadResult.Settings.OfflineTranslationEngine);
        Assert.Equal(
            [CaptureToolbarFeature.Text, CaptureToolbarFeature.Save],
            loadResult.Settings.VisibleCaptureToolbarFeatures);
        Assert.Equal(
            CaptureToolbarFeature.Save,
            loadResult.Settings.CaptureToolbarFeatureOrder[0]);
        Assert.Equal(
            CaptureToolbarFeature.Text,
            loadResult.Settings.CaptureToolbarFeatureOrder[1]);
        Assert.Equal(
            CaptureToolbarRowCount.Two,
            loadResult.Settings.CaptureToolbarRows);
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
        Assert.Equal(9, loadResult.Settings.SettingsVersion);
        Assert.Equal(
            Enum.GetValues<CaptureToolbarFeature>(),
            loadResult.Settings.VisibleCaptureToolbarFeatures);
    }

    [Fact]
    public void PreservesAnExplicitlyEmptyCaptureToolbarAndNormalizesInvalidValues()
    {
        var empty = (AppSettings.CreateDefault() with
        {
            VisibleCaptureToolbarFeatures = [],
        }).Normalize();
        Assert.Empty(empty.VisibleCaptureToolbarFeatures);

        var normalized = (AppSettings.CreateDefault() with
        {
            VisibleCaptureToolbarFeatures =
            [
                CaptureToolbarFeature.Text,
                CaptureToolbarFeature.Text,
                (CaptureToolbarFeature)999,
                CaptureToolbarFeature.Save,
            ],
            CaptureToolbarFeatureOrder =
            [
                CaptureToolbarFeature.Text,
                CaptureToolbarFeature.Text,
                (CaptureToolbarFeature)999,
            ],
            CaptureToolbarRows = (CaptureToolbarRowCount)999,
        }).Normalize();

        Assert.Equal(
            [CaptureToolbarFeature.Text, CaptureToolbarFeature.Save],
            normalized.VisibleCaptureToolbarFeatures);
        Assert.Equal(
            CaptureToolbarFeature.Text,
            normalized.CaptureToolbarFeatureOrder[0]);
        Assert.Equal(
            Enum.GetValues<CaptureToolbarFeature>().Length,
            normalized.CaptureToolbarFeatureOrder.Length);
        Assert.Equal(
            CaptureToolbarRowCount.One,
            normalized.CaptureToolbarRows);
    }

    [Fact]
    public void InvalidCaptureToolbarPositionReturnsToAutomaticPlacement()
    {
        var normalized = (AppSettings.CreateDefault() with
        {
            CaptureToolbarPositionXRatio = 0.4,
            CaptureToolbarPositionYRatio = 2,
        }).Normalize();

        Assert.Equal(-1, normalized.CaptureToolbarPositionXRatio);
        Assert.Equal(-1, normalized.CaptureToolbarPositionYRatio);
    }

    [Fact]
    public void LegacyToolbarSplitsCopyTextFromVisibleTextRecognition()
    {
        var normalized = (AppSettings.CreateDefault() with
        {
            SettingsVersion = 5,
            VisibleCaptureToolbarFeatures =
            [
                CaptureToolbarFeature.TextRecognition,
                CaptureToolbarFeature.Translation,
            ],
        }).Normalize();

        Assert.Equal(9, normalized.SettingsVersion);
        Assert.Equal(
            [
                CaptureToolbarFeature.TextRecognition,
                CaptureToolbarFeature.Translation,
                CaptureToolbarFeature.CopyRecognizedText,
                CaptureToolbarFeature.Number,
                CaptureToolbarFeature.PrivacyRedaction,
            ],
            normalized.VisibleCaptureToolbarFeatures);
    }

    [Fact]
    public void LegacyToolbarDoesNotEnableCopyTextWhenRecognitionWasHidden()
    {
        var normalized = (AppSettings.CreateDefault() with
        {
            SettingsVersion = 5,
            VisibleCaptureToolbarFeatures = [CaptureToolbarFeature.Save],
        }).Normalize();

        Assert.Equal(
            [
                CaptureToolbarFeature.Save,
                CaptureToolbarFeature.Number,
                CaptureToolbarFeature.PrivacyRedaction,
            ],
            normalized.VisibleCaptureToolbarFeatures);
    }

    [Fact]
    public void UpgradeEnablesNewToolsOnceAndPreservesLaterOptOut()
    {
        var upgraded = (AppSettings.CreateDefault() with
        {
            SettingsVersion = 6,
            VisibleCaptureToolbarFeatures = [CaptureToolbarFeature.Save],
        }).Normalize();

        Assert.Equal(9, upgraded.SettingsVersion);
        Assert.Equal(
            [
                CaptureToolbarFeature.Save,
                CaptureToolbarFeature.Number,
                CaptureToolbarFeature.PrivacyRedaction,
            ],
            upgraded.VisibleCaptureToolbarFeatures);

        var optedOut = (upgraded with
        {
            VisibleCaptureToolbarFeatures = [CaptureToolbarFeature.Save],
        }).Normalize();

        Assert.Equal(
            [CaptureToolbarFeature.Save],
            optedOut.VisibleCaptureToolbarFeatures);
    }

    [Fact]
    public void NullCaptureToolbarConfigurationUsesAllFeatures()
    {
        var normalized = (AppSettings.CreateDefault() with
        {
            VisibleCaptureToolbarFeatures = null!,
        }).Normalize();

        Assert.Equal(
            Enum.GetValues<CaptureToolbarFeature>(),
            normalized.VisibleCaptureToolbarFeatures);
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
