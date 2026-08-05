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
            OcrHotKey = "Ctrl+Alt+S",
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

    [Theory]
    [InlineData("F1", 0x70u)]
    [InlineData("F12", 0x7Bu)]
    [InlineData("F24", 0x87u)]
    [InlineData("PrintScreen", 0x2Cu)]
    [InlineData("Numpad0", 0x60u)]
    [InlineData("Numpad7", 0x67u)]
    public void AllowsDedicatedKeyboardKeysWithoutAModifier(
        string configured,
        uint expectedVirtualKey)
    {
        var parsed = HotKeyGesture.TryParse(
            configured,
            out var gesture,
            out var errorMessage);

        Assert.True(parsed, errorMessage);
        Assert.Equal(HotKeyModifiers.None, gesture.Modifiers);
        Assert.Equal(expectedVirtualKey, gesture.VirtualKey);
        Assert.Equal(configured, gesture.ToString());
        Assert.False(gesture.IsSystemReserved(out _));
    }

    [Fact]
    public void RegistersAStandaloneFunctionKeyShortcut()
    {
        var settings = AppSettings.CreateDefault() with
        {
            RegionCaptureHotKey = "F2",
        };

        var bindings = HotKeyConfiguration.CreateBindings(settings);
        var validation = HotKeyConfiguration.Validate(bindings);

        Assert.Contains(
            bindings,
            binding => binding.Action == HotKeyAction.RegionCapture &&
                       binding.Gesture == new HotKeyGesture(HotKeyModifiers.None, 0x71));
        Assert.True(validation.IsValid, validation.ErrorMessage);
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
    public void DoesNotRegisterTheLegacyScrollingCaptureShortcut()
    {
        var settings = AppSettings.CreateDefault() with
        {
            ScrollCaptureHotKey = "Ctrl+Alt+L",
        };

        var bindings = HotKeyConfiguration.CreateBindings(settings);

        Assert.DoesNotContain(
            bindings,
            binding => binding.Action == HotKeyAction.ScrollCapture);
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

    [Fact]
    public void RegistersVideoRecordingShortcut()
    {
        var settings = AppSettings.CreateDefault() with
        {
            VideoRecordingHotKey = "Ctrl+Alt+R",
        };

        var bindings = HotKeyConfiguration.CreateBindings(settings);

        Assert.Contains(
            bindings,
            binding => binding.Action == HotKeyAction.VideoRecording &&
                       binding.Gesture.ToString() == "Ctrl+Alt+R");
    }

    [Fact]
    public void EmptyVideoRecordingShortcutIsNotRegistered()
    {
        var settings = AppSettings.CreateDefault() with
        {
            VideoRecordingHotKey = "   ",
        };

        var normalized = settings.Normalize();
        var bindings = HotKeyConfiguration.CreateBindings(normalized);

        Assert.Equal(string.Empty, normalized.VideoRecordingHotKey);
        Assert.DoesNotContain(
            bindings,
            binding => binding.Action == HotKeyAction.VideoRecording);
    }

    [Fact]
    public void RejectsVideoRecordingShortcutUsedByAnotherAction()
    {
        var settings = AppSettings.CreateDefault() with
        {
            VideoRecordingHotKey = "Ctrl+Alt+S",
        };

        var validation = HotKeyConfiguration.Validate(
            HotKeyConfiguration.CreateBindings(settings));

        Assert.False(validation.IsValid);
        Assert.Contains("重复", validation.ErrorMessage);
    }

    [Fact]
    public void LegacyOcrSettingRegistersTheTranslationAction()
    {
        var settings = AppSettings.CreateDefault() with
        {
            RegionCaptureHotKey = string.Empty,
            OcrHotKey = "Ctrl+Alt+T",
        };

        var bindings = HotKeyConfiguration.CreateBindings(settings);

        Assert.Contains(
            bindings,
            binding => binding.Action == HotKeyAction.RecognizeText &&
                       binding.Gesture.ToString() == "Ctrl+Alt+T");
    }

    [Fact]
    public void InvalidLegacyOcrSettingUsesTheTranslationDisplayName()
    {
        var settings = AppSettings.CreateDefault() with
        {
            OcrHotKey = "T",
        };

        var exception = Assert.Throws<ArgumentException>(
            () => HotKeyConfiguration.CreateBindings(settings));

        Assert.Contains("翻译", exception.Message);
    }

    [Theory]
    [InlineData("鼠标后退键", "鼠标后退键", false)]
    [InlineData("MouseForward", "鼠标前进键", false)]
    [InlineData("鼠标左键", "长按鼠标左键", true)]
    [InlineData("长按滚轮键", "长按鼠标中键", true)]
    [InlineData("Ctrl+MouseRight", "Ctrl+长按鼠标右键", true)]
    [InlineData("Alt+MouseLeft", "Alt+长按鼠标左键", true)]
    [InlineData("Shift+MouseBack", "Shift+鼠标后退键", false)]
    [InlineData("Ctrl+Alt+MouseForward", "Ctrl+Alt+鼠标前进键", false)]
    public void ParsesAndFormatsMouseShortcuts(
        string configured,
        string expected,
        bool requiresHold)
    {
        var parsed = HotKeyGesture.TryParse(
            configured,
            out var gesture,
            out var errorMessage);

        Assert.True(parsed, errorMessage);
        Assert.True(gesture.IsMouseButton);
        Assert.Equal(requiresHold, gesture.RequiresHold);
        Assert.Equal(expected, gesture.ToString());
    }

    [Fact]
    public void MouseShortcutCanBeUsedWithoutAKeyboardModifier()
    {
        var settings = AppSettings.CreateDefault() with
        {
            RegionCaptureHotKey = "MouseBack",
        };

        var bindings = HotKeyConfiguration.CreateBindings(settings);

        Assert.Contains(
            bindings,
            binding => binding.Action == HotKeyAction.RegionCapture &&
                       binding.Gesture.ToString() == "鼠标后退键");
    }
}
