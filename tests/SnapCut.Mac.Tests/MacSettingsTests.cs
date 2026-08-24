using SnapCut.Mac.App;

namespace SnapCut.Mac.Tests;

public sealed class MacSettingsTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"SnapCut-Mac-Settings-{Guid.NewGuid():N}");

    [Fact]
    public void DefaultsMatchTheWindowsToolbarFeatureOrder()
    {
        Assert.Equal(
        [
            "Shape", "Arrow", "Emoji", "Number", "Brush", "Text", "Mosaic",
            "VideoRecording", "Save", "ScrollCapture", "RecognizeText",
            "CopyRecognizedText", "Translation", "PrivacyRedaction", "PinImage",
            "UndoRedo", "QrRecognition",
        ],
            MacSettings.DefaultToolbarFeatures);
        Assert.Equal(1, new MacSettings().ToolbarRows);
    }

    [Fact]
    public void RoundTripsHotkeysAndPreviewPreference()
    {
        var store = new MacSettingsStore(_directory);
        var expected = new MacSettings
        {
            CaptureHotkey = new MacHotkeyGesture(
                2,
                MacHotkeyModifiers.Command | MacHotkeyModifiers.Option,
                "⌘⌥D"),
            ScrollHotkey = MacHotkeyGesture.ScrollDefault,
            HistoryLimit = 24,
            ShowPreviewAfterCapture = false,
            AnnotationColor = "#34C759",
            AnnotationWidth = 6,
            TranslationEndpoint = "https://example.com/v1/chat/completions",
            TranslationModel = "test-model",
            TranslationTargetLanguage = "ja",
            SendTextToOnlineTranslation = true,
            OfflineTranslationConfigPath = "/tmp/model/config.yml",
            VisibleToolbarFeatures = ["Shape", "PrivacyRedaction"],
            ToolbarRows = 1,
        };

        store.Save(expected);
        var actual = store.Load();

        Assert.Equal(expected.CaptureHotkey, actual.CaptureHotkey);
        Assert.Equal(expected.ScrollHotkey, actual.ScrollHotkey);
        Assert.Equal(24, actual.HistoryLimit);
        Assert.False(actual.ShowPreviewAfterCapture);
        Assert.Equal("#34C759", actual.AnnotationColor);
        Assert.Equal(6, actual.AnnotationWidth);
        Assert.Equal("test-model", actual.TranslationModel);
        Assert.Equal("ja", actual.TranslationTargetLanguage);
        Assert.True(actual.SendTextToOnlineTranslation);
        Assert.Equal(["Shape", "PrivacyRedaction"], actual.VisibleToolbarFeatures);
        Assert.Equal(1, actual.ToolbarRows);
    }

    [Fact]
    public void InvalidJsonFallsBackToDefaults()
    {
        var store = new MacSettingsStore(_directory);
        Directory.CreateDirectory(_directory);
        File.WriteAllText(store.SettingsPath, "{not-json");

        var actual = store.Load();

        Assert.Equal(MacHotkeyGesture.CaptureDefault, actual.CaptureHotkey);
        Assert.Equal(MacHotkeyGesture.ScrollDefault, actual.ScrollHotkey);
    }

    [Fact]
    public void LoadNormalizesToolbarOrderAndAppendsNewFeatures()
    {
        var store = new MacSettingsStore(_directory);
        Directory.CreateDirectory(_directory);
        File.WriteAllText(
            store.SettingsPath,
            "{\"ToolbarFeatureOrder\":[\"Translation\",\"Translation\",\"Unknown\"]}");

        var actual = store.Load();

        Assert.Equal("Translation", actual.ToolbarFeatureOrder[0]);
        Assert.Equal(
            MacSettings.DefaultToolbarFeatures.Length,
            actual.ToolbarFeatureOrder.Length);
        Assert.Contains("Shape", actual.ToolbarFeatureOrder);
        Assert.Contains("Save", actual.ToolbarFeatureOrder);
        Assert.Contains("ScrollCapture", actual.ToolbarFeatureOrder);
    }

    [Fact]
    public void NullHotkeysAndInvalidHistoryLimitAreNormalized()
    {
        var store = new MacSettingsStore(_directory);
        Directory.CreateDirectory(_directory);
        File.WriteAllText(
            store.SettingsPath,
            """
            {
              "CaptureHotkey": null,
              "ScrollHotkey": null,
              "HistoryLimit": -20,
              "AnnotationColor": "invalid",
              "AnnotationWidth": 99
            }
            """);

        var actual = store.Load();

        Assert.Equal(MacHotkeyGesture.CaptureDefault, actual.CaptureHotkey);
        Assert.Equal(MacHotkeyGesture.ScrollDefault, actual.ScrollHotkey);
        Assert.Equal(1, actual.HistoryLimit);
        Assert.Equal("#FF3B30", actual.AnnotationColor);
        Assert.Equal(10, actual.AnnotationWidth);
    }

    [Fact]
    public void ExistingToolbarCustomizationIsPreservedAfterMigrationMarkerIsSaved()
    {
        var store = new MacSettingsStore(_directory);
        var expected = new MacSettings
        {
            VisibleToolbarFeatures = ["Shape", "Mosaic"],
            ToolbarFeatureOrder = [
                "Mosaic", "Shape", "Arrow", "Emoji", "Number", "Brush", "Text",
                "VideoRecording", "Save", "ScrollCapture", "RecognizeText",
                "CopyRecognizedText", "Translation", "PrivacyRedaction", "PinImage",
                "UndoRedo", "QrRecognition",
            ],
            ArrowStyle = "Hollow",
            ToolbarPositionXRatio = 0.25,
            ToolbarPositionYRatio = 0.75,
        };

        store.Save(expected);
        var actual = store.Load();

        Assert.Equal(["Shape", "Mosaic"], actual.VisibleToolbarFeatures);
        Assert.Equal("Mosaic", actual.ToolbarFeatureOrder[0]);
        Assert.Equal("Hollow", actual.ArrowStyle);
        Assert.Equal(0.25, actual.ToolbarPositionXRatio);
        Assert.Equal(0.75, actual.ToolbarPositionYRatio);
    }

    [Fact]
    public void InvalidArrowStyleAndToolbarPositionAreNormalized()
    {
        var store = new MacSettingsStore(_directory);
        Directory.CreateDirectory(_directory);
        File.WriteAllText(
            store.SettingsPath,
            """
            {
              "ToolbarFeaturesInitialized": true,
              "ArrowStyle": "Unknown",
              "ToolbarPositionXRatio": 2,
              "ToolbarPositionYRatio": -2
            }
            """);

        var actual = store.Load();

        Assert.Equal("Filled", actual.ArrowStyle);
        Assert.Equal(-1, actual.ToolbarPositionXRatio);
        Assert.Equal(-1, actual.ToolbarPositionYRatio);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
