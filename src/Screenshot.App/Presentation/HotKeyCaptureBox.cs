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
    private const uint VirtualKeyBack = 0x08;
    private const uint VirtualKeyEscape = 0x1B;
    private const uint VirtualKeyDelete = 0x2E;
    private const uint VirtualKeyShift = 0x10;
    private const uint VirtualKeyControl = 0x11;
    private const uint VirtualKeyAlt = 0x12;
    private const uint VirtualKeyLeftWindows = 0x5B;
    private const uint VirtualKeyRightWindows = 0x5C;
    private const uint VirtualKeyLeftShift = 0xA0;
    private const uint VirtualKeyRightShift = 0xA1;
    private const uint VirtualKeyLeftControl = 0xA2;
    private const uint VirtualKeyRightControl = 0xA3;
    private const uint VirtualKeyLeftAlt = 0xA4;
    private const uint VirtualKeyRightAlt = 0xA5;

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

    internal void ProcessCapturedVirtualKey(
        uint virtualKey,
        HotKeyModifiers modifiers)
    {
        if (virtualKey == VirtualKeyEscape && modifiers == HotKeyModifiers.None)
        {
            HotKeyCaptureCanceled?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (virtualKey is VirtualKeyBack or VirtualKeyDelete &&
            modifiers == HotKeyModifiers.None)
        {
            ClearGesture();
            return;
        }

        if (IsModifierVirtualKey(virtualKey) ||
            !TryFormatGesture(virtualKey, modifiers, out var gesture))
        {
            return;
        }

        Text = gesture;
        HotKeyCaptured?.Invoke(this, new HotKeyCapturedEventArgs(Text));
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

    internal static bool TryFormatGesture(
        uint virtualKey,
        HotKeyModifiers modifiers,
        out string gesture)
    {
        if (modifiers == HotKeyModifiers.None || IsModifierVirtualKey(virtualKey))
        {
            gesture = string.Empty;
            return false;
        }

        var capturedGesture = new HotKeyGesture(modifiers, virtualKey);
        var formattedGesture = capturedGesture.ToString();
        if (!HotKeyGesture.TryParse(
                formattedGesture,
                out var parsedGesture,
                out _) ||
            parsedGesture != capturedGesture)
        {
            gesture = string.Empty;
            return false;
        }

        gesture = formattedGesture;
        return true;
    }

    private static bool IsModifierKey(Key key)
    {
        return key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or
            Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin;
    }

    private static bool IsModifierVirtualKey(uint virtualKey)
    {
        return virtualKey is
            VirtualKeyShift or VirtualKeyLeftShift or VirtualKeyRightShift or
            VirtualKeyControl or VirtualKeyLeftControl or VirtualKeyRightControl or
            VirtualKeyAlt or VirtualKeyLeftAlt or VirtualKeyRightAlt or
            VirtualKeyLeftWindows or VirtualKeyRightWindows;
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
