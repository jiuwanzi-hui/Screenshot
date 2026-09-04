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
    private bool _captureRequested;
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
        // Shortcut fields represent physical keys, not text. Disabling IME
        // processing here prevents a Chinese composition window from
        // consuming the digit before the key-capture handler sees it.
        InputMethod.SetIsInputMethodEnabled(this, false);
    }

    public event EventHandler<HotKeyCapturedEventArgs>? HotKeyCaptured;

    public event EventHandler? HotKeyCaptureCanceled;

    public event EventHandler? HotKeyCaptureRequested;

    internal void RequestCapture() => _captureRequested = true;

    internal bool ConsumeCaptureRequest()
    {
        var requested = _captureRequested;
        _captureRequested = false;
        return requested;
    }

    public void ClearGesture()
    {
        Text = string.Empty;
        HotKeyCaptured?.Invoke(this, new HotKeyCapturedEventArgs(string.Empty));
    }

    internal void ProcessCapturedVirtualKey(
        uint virtualKey,
        HotKeyModifiers modifiers)
    {
        ProcessCapturedGesture(new HotKeyGesture(modifiers, virtualKey));
    }

    internal void ProcessCapturedGesture(HotKeyGesture capturedGesture)
    {
        var virtualKey = capturedGesture.VirtualKey;
        var modifiers = capturedGesture.Modifiers;
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
            !TryFormatCapturedGesture(capturedGesture, out var gesture))
        {
            return;
        }

        Text = gesture;
        HotKeyCaptured?.Invoke(this, new HotKeyCapturedEventArgs(Text));
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        if (_captureRequested)
        {
            SelectAll();
        }
    }

    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        RequestCapture();
        if (!IsKeyboardFocusWithin)
        {
            Focus();
            e.Handled = true;
        }
        else
        {
            HotKeyCaptureRequested?.Invoke(this, EventArgs.Empty);
        }

        base.OnPreviewMouseLeftButtonDown(e);
    }

    protected override void OnPreviewKeyDown(WpfKeyEventArgs e)
    {
        var key = e.Key switch
        {
            Key.System => e.SystemKey,
            Key.ImeProcessed => e.ImeProcessedKey,
            _ => e.Key,
        };

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

        if (!TryCreateGesture(key, Keyboard.Modifiers, out var gesture) &&
            !TryCreateStandaloneLetterGesture(key, Keyboard.Modifiers, out gesture))
        {
            e.Handled = true;
            return;
        }

        Text = gesture.ToString();
        HotKeyCaptured?.Invoke(this, new HotKeyCapturedEventArgs(Text));
        e.Handled = true;
    }

    protected override void OnPreviewTextInput(TextCompositionEventArgs e)
    {
        // IMEs can emit TextInput after the physical key event. Keep shortcut
        // capture independent of the current Chinese/English IME state and
        // never allow committed text to reach the canvas behind this box.
        if (IsKeyboardFocusWithin)
        {
            var text = e.Text?.Trim() ?? string.Empty;
            if (text.Length == 1 && IsAsciiDigit(text[0]))
            {
                Text = text.ToUpperInvariant();
                HotKeyCaptured?.Invoke(this, new HotKeyCapturedEventArgs(Text));
            }

            e.Handled = true;
            return;
        }

        base.OnPreviewTextInput(e);
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

        if (!TryGetVirtualKey(key, out var virtualKey) ||
            (modifiers == HotKeyModifiers.None &&
             !HotKeyGesture.AllowsStandaloneKeyboardShortcut(virtualKey)))
        {
            return false;
        }

        gesture = new HotKeyGesture(modifiers, virtualKey);
        return true;
    }

    private bool TryCreateStandaloneLetterGesture(
        Key key,
        ModifierKeys keyboardModifiers,
        out HotKeyGesture gesture)
    {
        gesture = default;
        if (!IsCompletionCaptureSetting && !IsToolbarFeatureCapture ||
            keyboardModifiers != ModifierKeys.None ||
            !TryGetVirtualKey(key, out var virtualKey) ||
            !IsStandaloneToolbarDigitKey(virtualKey))
        {
            return false;
        }

        gesture = new HotKeyGesture(HotKeyModifiers.None, virtualKey);
        return true;
    }

    private bool TryFormatCapturedGesture(
        HotKeyGesture capturedGesture,
        out string gesture)
    {
        if (TryFormatGesture(capturedGesture, out gesture))
        {
            return true;
        }

        if ((IsCompletionCaptureSetting || IsToolbarFeatureCapture) &&
            capturedGesture.Modifiers == HotKeyModifiers.None &&
            IsStandaloneToolbarDigitKey(capturedGesture.VirtualKey))
        {
            gesture = capturedGesture.ToString();
            return true;
        }

        gesture = string.Empty;
        return false;
    }

    private bool IsCompletionCaptureSetting =>
        string.Equals(
            Tag?.ToString(),
            "CompleteCaptureHotKey",
            StringComparison.Ordinal);

    private bool IsToolbarFeatureCapture => Tag is CaptureToolbarFeature ||
        Enum.TryParse<CaptureToolbarFeature>(Tag?.ToString(), out _);

    private static bool IsStandaloneToolbarDigitKey(uint virtualKey) =>
        virtualKey is >= '0' and <= '9' or >= 0x60 and <= 0x69;

    private static bool IsAsciiDigit(char value) => value is >= '0' and <= '9';

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
        return TryFormatGesture(
            new HotKeyGesture(modifiers, virtualKey),
            out gesture);
    }

    internal static bool TryFormatGesture(
        HotKeyGesture capturedGesture,
        out string gesture)
    {
        var virtualKey = capturedGesture.VirtualKey;
        var modifiers = capturedGesture.Modifiers;
        if ((modifiers == HotKeyModifiers.None &&
             !capturedGesture.IsMouseButton &&
             !HotKeyGesture.AllowsStandaloneKeyboardShortcut(virtualKey)) ||
            IsModifierVirtualKey(virtualKey))
        {
            gesture = string.Empty;
            return false;
        }

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

        if (key is >= Key.NumPad0 and <= Key.NumPad9)
        {
            virtualKey = (uint)(0x60 + (key - Key.NumPad0));
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
