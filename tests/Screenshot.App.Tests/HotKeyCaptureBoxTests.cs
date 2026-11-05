using System.Windows.Input;
using System.Windows.Interop;
using Screenshot.App.Core;
using Screenshot.App.Presentation;

namespace Screenshot.App.Tests;

public sealed class HotKeyCaptureBoxTests
{
    [Fact]
    public void CompletionCaptureAcceptsEveryStandaloneLetterAndCanBeChangedAgain()
    {
        WpfTestHost.Invoke(() =>
        {
            var box = new HotKeyCaptureBox { Tag = "CompleteCaptureHotKey", Text = "Ctrl+C" };
            string? captured = null;
            box.HotKeyCaptured += (_, e) => captured = e.Gesture;
            Assert.False(InputMethod.GetIsInputMethodEnabled(box));
            for (var key = 'A'; key <= 'Z'; key++)
            {
                captured = null;
                box.ProcessCapturedVirtualKey(key, HotKeyModifiers.None);
                Assert.Equal(key.ToString(), captured);
                Assert.Equal(key.ToString(), box.Text);
            }

            box.ProcessCapturedVirtualKey('C', HotKeyModifiers.Control);
            Assert.Equal("Ctrl+C", captured);
        });
    }

    [Theory]
    [InlineData(Key.A, "A")]
    [InlineData(Key.C, "C")]
    [InlineData(Key.Z, "Z")]
    public void CompletionCaptureAcceptsLettersThroughWpfInput(Key key, string expected)
    {
        WpfTestHost.Invoke(() =>
        {
            var box = new HotKeyCaptureBox { Tag = "CompleteCaptureHotKey", Text = "Ctrl+C" };
            using var source = new HwndSource(new HwndSourceParameters("Offscreen completion shortcut"));
            source.RootVisual = box;
            string? captured = null;
            box.HotKeyCaptured += (_, e) => captured = e.Gesture;
            box.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key)
            {
                RoutedEvent = Keyboard.PreviewKeyDownEvent,
            });
            Assert.Equal(expected, captured);
            Assert.Equal(expected, box.Text);
        });
    }

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

    [Theory]
    [InlineData(Key.F1, "F1")]
    [InlineData(Key.F12, "F12")]
    [InlineData(Key.F24, "F24")]
    [InlineData(Key.PrintScreen, "PrintScreen")]
    [InlineData(Key.NumPad3, "Numpad3")]
    public void CapturesDedicatedKeysWithoutAModifier(Key key, string expected)
    {
        var formatted = HotKeyCaptureBox.TryFormatGesture(
            key,
            ModifierKeys.None,
            out var gesture);

        Assert.True(formatted);
        Assert.Equal(expected, gesture);
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

    [Fact]
    public void CapturesACombinationReportedByTheGlobalKeyboardHook()
    {
        WpfTestHost.Invoke(() =>
        {
            var captureBox = new HotKeyCaptureBox();
            string? capturedGesture = null;
            captureBox.HotKeyCaptured += (_, eventArgs) =>
                capturedGesture = eventArgs.Gesture;

            captureBox.ProcessCapturedVirtualKey(
                'A',
                HotKeyModifiers.Alt);

            Assert.Equal("Alt+A", captureBox.Text);
            Assert.Equal("Alt+A", capturedGesture);
        });
    }

    [Fact]
    public void IgnoresAReportedKeyWithoutAModifier()
    {
        WpfTestHost.Invoke(() =>
        {
            var captureBox = new HotKeyCaptureBox
            {
                Text = "Ctrl+Alt+S",
            };
            var captureRaised = false;
            captureBox.HotKeyCaptured += (_, _) => captureRaised = true;

            captureBox.ProcessCapturedVirtualKey(
                'A',
                HotKeyModifiers.None);

            Assert.Equal("Ctrl+Alt+S", captureBox.Text);
            Assert.False(captureRaised);
        });
    }

    [Theory]
    [InlineData(HotKeyGesture.VirtualKeyMouseBack, HotKeyModifiers.None, "鼠标后退键")]
    [InlineData(HotKeyGesture.VirtualKeyMouseLeft, HotKeyModifiers.None, "长按鼠标左键")]
    [InlineData(HotKeyGesture.VirtualKeyMouseMiddle, HotKeyModifiers.Control, "Ctrl+长按鼠标中键")]
    [InlineData(HotKeyGesture.VirtualKeyMouseForward, HotKeyModifiers.Alt | HotKeyModifiers.Shift, "Alt+Shift+鼠标前进键")]
    public void CapturesMouseButtonsReportedByTheGlobalHook(
        uint virtualKey,
        HotKeyModifiers modifiers,
        string expected)
    {
        WpfTestHost.Invoke(() =>
        {
            var captureBox = new HotKeyCaptureBox();
            string? capturedGesture = null;
            captureBox.HotKeyCaptured += (_, eventArgs) =>
                capturedGesture = eventArgs.Gesture;

            captureBox.ProcessCapturedGesture(
                new HotKeyGesture(modifiers, virtualKey));

            Assert.Equal(expected, captureBox.Text);
            Assert.Equal(expected, capturedGesture);
        });
    }
}
