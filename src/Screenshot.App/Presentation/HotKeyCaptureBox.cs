using System.Windows.Input;
using Screenshot.App.Core;
using WpfKeyEventArgs = System.Windows.Input.KeyEventArgs;
using WpfTextBox = System.Windows.Controls.TextBox;

namespace Screenshot.App.Presentation;

public sealed class HotKeyCapturedEventArgs : EventArgs
{
    public HotKeyCapturedEventArgs(string gesture)
    {
        Gesture = gesture;
    }

    public string Gesture { get; }
}

public sealed class HotKeyCaptureBox : WpfTextBox
{
    public HotKeyCaptureBox()
    {
        IsReadOnly = true;
        Cursor = System.Windows.Input.Cursors.Hand;
        VerticalContentAlignment = System.Windows.VerticalAlignment.Center;
    }

    public event EventHandler<HotKeyCapturedEventArgs>? HotKeyCaptured;

    public event EventHandler? HotKeyCaptureCanceled;

    public void ClearGesture()
    {
        Text = string.Empty;
        HotKeyCaptured?.Invoke(this, new HotKeyCapturedEventArgs(string.Empty));
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        SelectAll();
    }

    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (!IsKeyboardFocusWithin)
        {
            Focus();
            e.Handled = true;
        }

        base.OnPreviewMouseLeftButtonDown(e);
    }

    protected override void OnPreviewKeyDown(WpfKeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (key == Key.Escape && Keyboard.Modifiers == ModifierKeys.None)
        {
            HotKeyCaptureCanceled?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
            return;
        }

        if (key is Key.Back or Key.Delete &&
            Keyboard.Modifiers == ModifierKeys.None)
        {
            ClearGesture();
            e.Handled = true;
            return;
        }

        if (IsModifierKey(key))
        {
            e.Handled = true;
            return;
        }

        if (!TryCreateGesture(key, Keyboard.Modifiers, out var gesture))
        {
            e.Handled = true;
            return;
        }

        Text = gesture.ToString();
        HotKeyCaptured?.Invoke(this, new HotKeyCapturedEventArgs(Text));
        e.Handled = true;
    }

    protected override void OnPreviewKeyUp(WpfKeyEventArgs e)
    {
        e.Handled = true;
    }

    private static bool TryCreateGesture(
        Key key,
        ModifierKeys keyboardModifiers,
        out HotKeyGesture gesture)
    {
        gesture = default;
        var modifiers = HotKeyModifiers.None;

        if ((keyboardModifiers & ModifierKeys.Control) != 0)
        {
            modifiers |= HotKeyModifiers.Control;
        }

        if ((keyboardModifiers & ModifierKeys.Alt) != 0)
        {
            modifiers |= HotKeyModifiers.Alt;
        }

        if ((keyboardModifiers & ModifierKeys.Shift) != 0)
        {
            modifiers |= HotKeyModifiers.Shift;
        }

        if ((keyboardModifiers & ModifierKeys.Windows) != 0)
        {
            modifiers |= HotKeyModifiers.Windows;
        }

        if (modifiers == HotKeyModifiers.None || !TryGetVirtualKey(key, out var virtualKey))
        {
            return false;
        }

        gesture = new HotKeyGesture(modifiers, virtualKey);
        return true;
    }

    public static bool TryFormatGesture(
        Key key,
        ModifierKeys keyboardModifiers,
        out string gesture)
    {
        if (!TryCreateGesture(key, keyboardModifiers, out var capturedGesture))
        {
            gesture = string.Empty;
            return false;
        }

        gesture = capturedGesture.ToString();
        return true;
    }

    private static bool IsModifierKey(Key key)
    {
        return key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or
            Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin;
    }

    private static bool TryGetVirtualKey(Key key, out uint virtualKey)
    {
        if (key is >= Key.A and <= Key.Z)
        {
            virtualKey = (uint)('A' + (key - Key.A));
            return true;
        }

        if (key is >= Key.D0 and <= Key.D9)
        {
            virtualKey = (uint)('0' + (key - Key.D0));
            return true;
        }

        if (key is >= Key.F1 and <= Key.F24)
        {
            virtualKey = (uint)(0x70 + (key - Key.F1));
            return true;
        }

        virtualKey = key switch
        {
            Key.Tab => 0x09,
            Key.Escape => 0x1B,
            Key.Space => 0x20,
            Key.Delete => 0x2E,
            Key.OemComma => 0xBC,
            Key.OemPeriod => 0xBE,
            Key.OemMinus => 0xBD,
            Key.OemPlus => 0xBB,
            Key.OemSemicolon => 0xBA,
            Key.OemQuestion => 0xBF,
            Key.OemTilde => 0xC0,
            Key.OemOpenBrackets => 0xDB,
            Key.OemPipe => 0xDC,
            Key.OemCloseBrackets => 0xDD,
            Key.OemQuotes => 0xDE,
            Key.Insert => 0x2D,
            Key.Home => 0x24,
            Key.End => 0x23,
            Key.PageUp => 0x21,
            Key.PageDown => 0x22,
            Key.Left => 0x25,
            Key.Up => 0x26,
            Key.Right => 0x27,
            Key.Down => 0x28,
            Key.PrintScreen => 0x2C,
            _ => 0,
        };
        return virtualKey != 0;
    }
}
