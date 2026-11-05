using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Screenshot.App.Core;
using Screenshot.App.Infrastructure;

namespace Screenshot.App.Tests;

public sealed class FeatureIdleSettingsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OldStandbyPreferenceIsIgnoredWithoutResettingUserSettings(bool previouslyEnabled)
    {
        var directory = Path.Combine(Path.GetTempPath(), "SnapCut.IdleSettings." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "settings.json");
            var store = new SettingsStore(path);
            store.Save(AppSettings.CreateDefault() with
            {
                Theme = AppTheme.GlassInk, ToolbarScalePercent = 125,
                TranslationTargetLanguage = "ja", RegionCaptureHotKey = "Ctrl+Alt+F11",
                AutoOcrAfterCapture = true,
            });
            var expected = JsonNode.Parse(File.ReadAllText(path))!;
            var legacy = expected.DeepClone();
            legacy["keepCaptureUiReady"] = previouslyEnabled;
            File.WriteAllText(path, legacy.ToJsonString());

            var loaded = store.Load();
            Assert.Null(loaded.Warning);
            store.Save(loaded.Settings);
            Assert.True(JsonNode.DeepEquals(expected, JsonNode.Parse(File.ReadAllText(path))));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void BootstrapAlsoIgnoresTheRetiredStandbyPreference()
    {
        var bootstrap = new FeatureMessage("bootstrap") { Settings = AppSettings.CreateDefault() };
        var expected = JsonSerializer.SerializeToNode(bootstrap, FeatureJsonContext.Default.FeatureMessage)!;
        var legacy = expected.DeepClone();
        legacy["Settings"]!["KeepCaptureUiReady"] = true;
        var loaded = JsonSerializer.Deserialize(legacy, FeatureJsonContext.Default.FeatureMessage)!;
        Assert.True(JsonNode.DeepEquals(expected,
            JsonSerializer.SerializeToNode(loaded, FeatureJsonContext.Default.FeatureMessage)));
    }
}
