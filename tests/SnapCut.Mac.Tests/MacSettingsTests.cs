using SnapCut.Mac.App;

namespace SnapCut.Mac.Tests;

public sealed class MacSettingsTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"SnapCut-Mac-Settings-{Guid.NewGuid():N}");

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
        };

        store.Save(expected);
        var actual = store.Load();

        Assert.Equal(expected.CaptureHotkey, actual.CaptureHotkey);
        Assert.Equal(expected.ScrollHotkey, actual.ScrollHotkey);
        Assert.Equal(24, actual.HistoryLimit);
        Assert.False(actual.ShowPreviewAfterCapture);
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
              "HistoryLimit": -20
            }
            """);

        var actual = store.Load();

        Assert.Equal(MacHotkeyGesture.CaptureDefault, actual.CaptureHotkey);
        Assert.Equal(MacHotkeyGesture.ScrollDefault, actual.ScrollHotkey);
        Assert.Equal(1, actual.HistoryLimit);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
