using System.Windows.Input;
using Screenshot.App.Core;
using Screenshot.App.Presentation;

namespace Screenshot.App.Tests;

public sealed class ToolbarShortcutTests
{
    [Theory]
    [InlineData("1", "1", Key.D1, ModifierKeys.None)]
    [InlineData("ctrl+1", "Ctrl+1", Key.D1, ModifierKeys.Control)]
    [InlineData("ctrl+1", "Ctrl+1", Key.NumPad1, ModifierKeys.Control)]
    [InlineData("Alt+Numpad2", "Alt+2", Key.D2, ModifierKeys.Alt)]
    [InlineData("Shift+Ctrl+9", "Ctrl+Shift+9", Key.D9, ModifierKeys.Control | ModifierKeys.Shift)]
    public void ModifierDigitsSurviveSettingsAndMatchExactly(string configured, string expected, Key key, ModifierKeys modifiers)
    {
        var settings = (AppSettings.CreateDefault() with { CaptureToolbarFeatureShortcuts = new() { [CaptureToolbarFeature.CopyTable] = configured } }).Normalize();
        Assert.Equal(expected, settings.CaptureToolbarFeatureShortcuts[CaptureToolbarFeature.CopyTable]);
        Assert.True(CaptureToolbarShortcut.TryFind(settings.CaptureToolbarFeatureShortcuts, key, modifiers, out var feature));
        Assert.Equal(CaptureToolbarFeature.CopyTable, feature);
        Assert.False(CaptureToolbarShortcut.TryFind(settings.CaptureToolbarFeatureShortcuts, key, modifiers ^ ModifierKeys.Control, out _));
        WpfTestHost.Invoke(() =>
        {
            var viewModel = new SettingsViewModel(settings);
            Assert.Equal(expected, viewModel.CreateSettings().Normalize().CaptureToolbarFeatureShortcuts[CaptureToolbarFeature.CopyTable]);
        });
    }

    [Theory]
    [InlineData("Win+1")]
    [InlineData("Ctrl")]
    [InlineData("Ctrl+A")]
    public void RejectsReservedOrUnsupportedToolbarBindings(string value) => Assert.False(CaptureToolbarShortcut.TryNormalize(value, out _));

    [Fact]
    public void PhysicalCaptureKeepsCtrlAndDigitTogether()
    {
        WpfTestHost.Invoke(() =>
        {
            var box = new HotKeyCaptureBox { Tag = CaptureToolbarFeature.CopyTable };
            string? captured = null;
            box.HotKeyCaptured += (_, e) => captured = e.Gesture;
            box.ProcessCapturedVirtualKey('1', HotKeyModifiers.Control);
            Assert.Equal("Ctrl+1", captured);
            Assert.Equal("Ctrl+1", box.Text);
            var globalSettings = AppSettings.CreateDefault() with { OcrHotKey = captured! };
            var bindings = HotKeyConfiguration.CreateBindings(globalSettings);
            Assert.True(HotKeyConfiguration.Validate(bindings).IsValid);
            Assert.Equal(new HotKeyGesture(HotKeyModifiers.Control, '1'),
                Assert.Single(bindings, binding => binding.Action == HotKeyAction.RecognizeText).Gesture);
        });
    }
}
