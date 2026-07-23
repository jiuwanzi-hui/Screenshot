using System.Windows.Input;
using Screenshot.App.Presentation;

namespace Screenshot.App.Tests;

public sealed class HotKeyCaptureBoxTests
{
    [Fact]
    public void IsReadOnlyAndUsesAKeyboardCaptureSurface()
    {
        WpfTestHost.Invoke(() =>
        {
            var captureBox = new HotKeyCaptureBox();

            Assert.True(captureBox.IsReadOnly);
            Assert.Equal(Cursors.Hand, captureBox.Cursor);
        });
    }

    [Fact]
    public void FormatsThePressedKeyAndModifiersAsAGlobalHotKey()
    {
        var formatted = HotKeyCaptureBox.TryFormatGesture(
            Key.S,
            ModifierKeys.Control | ModifierKeys.Alt,
            out var gesture);

        Assert.True(formatted);
        Assert.Equal("Ctrl+Alt+S", gesture);
    }

    [Fact]
    public void RejectsKeysWithoutAModifier()
    {
        var formatted = HotKeyCaptureBox.TryFormatGesture(
            Key.S,
            ModifierKeys.None,
            out var gesture);

        Assert.False(formatted);
        Assert.Equal(string.Empty, gesture);
    }

    [Fact]
    public void FormatsPunctuationKeysUsedByCommonShortcutConfigurations()
    {
        var formatted = HotKeyCaptureBox.TryFormatGesture(
            Key.OemTilde,
            ModifierKeys.Control,
            out var gesture);

        Assert.True(formatted);
        Assert.Equal("Ctrl+Backtick", gesture);
    }

    [Fact]
    public void RaisesAnEmptyGestureWhenCleared()
    {
        WpfTestHost.Invoke(() =>
        {
            var captureBox = new HotKeyCaptureBox
            {
                Text = "Ctrl+Alt+S",
            };
            string? capturedGesture = null;
            captureBox.HotKeyCaptured += (_, eventArgs) =>
                capturedGesture = eventArgs.Gesture;

            captureBox.ClearGesture();

            Assert.Equal(string.Empty, captureBox.Text);
            Assert.Equal(string.Empty, capturedGesture);
        });
    }
}
