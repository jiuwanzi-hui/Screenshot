using Screenshot.App.Core;

namespace Screenshot.App.Tests;

public sealed class HotKeyConfigurationTests
{
    [Fact]
    public void ParsesAndFormatsAStandardShortcut()
    {
        var parsed = HotKeyGesture.TryParse(
            "control + alt + s",
            out var gesture,
            out var errorMessage);

        Assert.True(parsed);
        Assert.Equal(string.Empty, errorMessage);
        Assert.Equal("Ctrl+Alt+S", gesture.ToString());
    }

    [Fact]
    public void ParsesAndFormatsABacktickShortcut()
    {
        var parsed = HotKeyGesture.TryParse(
            "Ctrl+Backtick",
            out var gesture,
            out var errorMessage);

        Assert.True(parsed);
        Assert.Equal(string.Empty, errorMessage);
        Assert.Equal("Ctrl+Backtick", gesture.ToString());
    }

    [Fact]
    public void RejectsSystemReservedWindowsKeyShortcuts()
    {
        var settings = AppSettings.CreateDefault() with
        {
            RegionCaptureHotKey = "Win+S",
        };

        var validation = HotKeyConfiguration.Validate(
            HotKeyConfiguration.CreateBindings(settings));

        Assert.False(validation.IsValid);
        Assert.Contains("Win", validation.ErrorMessage);
    }

    [Fact]
    public void RejectsDuplicateShortcuts()
    {
        var settings = AppSettings.CreateDefault() with
        {
            ScrollCaptureHotKey = "Ctrl+Alt+S",
        };

        var validation = HotKeyConfiguration.Validate(
            HotKeyConfiguration.CreateBindings(settings));

        Assert.False(validation.IsValid);
        Assert.Contains("重复", validation.ErrorMessage);
    }

    [Fact]
    public void RejectsShortcutsWithoutAModifier()
    {
        var settings = AppSettings.CreateDefault() with
        {
            RegionCaptureHotKey = "S",
        };

        Assert.Throws<ArgumentException>(
            () => HotKeyConfiguration.CreateBindings(settings));
    }

    [Fact]
    public void OmitsEmptyShortcutsFromRegistration()
    {
        var settings = AppSettings.CreateDefault() with
        {
            RegionCaptureHotKey = string.Empty,
            ScrollCaptureHotKey = "   ",
        };

        var normalized = settings.Normalize();
        var bindings = HotKeyConfiguration.CreateBindings(normalized);
        var validation = HotKeyConfiguration.Validate(bindings);

        Assert.Equal(string.Empty, normalized.RegionCaptureHotKey);
        Assert.Equal(string.Empty, normalized.ScrollCaptureHotKey);
        Assert.DoesNotContain(
            bindings,
            binding => binding.Action is HotKeyAction.RegionCapture or HotKeyAction.ScrollCapture);
        Assert.True(validation.IsValid, validation.ErrorMessage);
    }

    [Fact]
    public void RegistersPinImageShortcut()
    {
        var settings = AppSettings.CreateDefault() with
        {
            PinHotKey = "Ctrl+Alt+P",
        };

        var bindings = HotKeyConfiguration.CreateBindings(settings);

        Assert.Contains(
            bindings,
            binding => binding.Action == HotKeyAction.PinImage &&
                       binding.Gesture.ToString() == "Ctrl+Alt+P");
    }
}
