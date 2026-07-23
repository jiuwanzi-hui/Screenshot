namespace Screenshot.App.Core;

[Flags]
public enum HotKeyModifiers : uint
{
    None = 0,
    Alt = 0x0001,
    Control = 0x0002,
    Shift = 0x0004,
    Windows = 0x0008,
}

public readonly record struct HotKeyGesture(HotKeyModifiers Modifiers, uint VirtualKey)
{
    private const uint VirtualKeyTab = 0x09;
    private const uint VirtualKeyEscape = 0x1B;
    private const uint VirtualKeySpace = 0x20;
    private const uint VirtualKeyPageUp = 0x21;
    private const uint VirtualKeyPageDown = 0x22;
    private const uint VirtualKeyEnd = 0x23;
    private const uint VirtualKeyHome = 0x24;
    private const uint VirtualKeyLeft = 0x25;
    private const uint VirtualKeyUp = 0x26;
    private const uint VirtualKeyRight = 0x27;
    private const uint VirtualKeyDown = 0x28;
    private const uint VirtualKeyPrintScreen = 0x2C;
    private const uint VirtualKeyInsert = 0x2D;
    private const uint VirtualKeyDelete = 0x2E;
    private const uint VirtualKeyF4 = 0x73;
    private const uint VirtualKeySemicolon = 0xBA;
    private const uint VirtualKeyPlus = 0xBB;
    private const uint VirtualKeyComma = 0xBC;
    private const uint VirtualKeyMinus = 0xBD;
    private const uint VirtualKeyPeriod = 0xBE;
    private const uint VirtualKeySlash = 0xBF;
    private const uint VirtualKeyBacktick = 0xC0;
    private const uint VirtualKeyLeftBracket = 0xDB;
    private const uint VirtualKeyBackslash = 0xDC;
    private const uint VirtualKeyRightBracket = 0xDD;
    private const uint VirtualKeyQuote = 0xDE;

    public static bool TryParse(string? value, out HotKeyGesture gesture, out string errorMessage)
    {
        gesture = default;
        errorMessage = string.Empty;

        if (string.IsNullOrWhiteSpace(value))
        {
            errorMessage = "快捷键不能为空。";
            return false;
        }

        var tokens = value.Split(
            '+',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (tokens.Length < 2)
        {
            errorMessage = "快捷键至少需要一个修饰键和一个普通按键。";
            return false;
        }

        var modifiers = HotKeyModifiers.None;

        for (var index = 0; index < tokens.Length - 1; index++)
        {
            if (!TryParseModifier(tokens[index], out var modifier))
            {
                errorMessage = $"无法识别修饰键“{tokens[index]}”。";
                return false;
            }

            if ((modifiers & modifier) != 0)
            {
                errorMessage = $"修饰键“{tokens[index]}”重复。";
                return false;
            }

            modifiers |= modifier;
        }

        if (modifiers == HotKeyModifiers.None)
        {
            errorMessage = "快捷键至少需要一个修饰键。";
            return false;
        }

        if (!TryParseKey(tokens[^1], out var virtualKey))
        {
            errorMessage = $"无法识别按键“{tokens[^1]}”。";
            return false;
        }

        gesture = new HotKeyGesture(modifiers, virtualKey);
        return true;
    }

    public override string ToString()
    {
        var parts = new List<string>();

        if ((Modifiers & HotKeyModifiers.Control) != 0)
        {
            parts.Add("Ctrl");
        }

        if ((Modifiers & HotKeyModifiers.Alt) != 0)
        {
            parts.Add("Alt");
        }

        if ((Modifiers & HotKeyModifiers.Shift) != 0)
        {
            parts.Add("Shift");
        }

        if ((Modifiers & HotKeyModifiers.Windows) != 0)
        {
            parts.Add("Win");
        }

        parts.Add(GetKeyName(VirtualKey));
        return string.Join("+", parts);
    }

    public bool IsSystemReserved(out string errorMessage)
    {
        if ((Modifiers & HotKeyModifiers.Windows) != 0)
        {
            errorMessage = "含 Win 键的系统快捷键不能由本软件接管。";
            return true;
        }

        if ((Modifiers & HotKeyModifiers.Alt) != 0 &&
            VirtualKey is VirtualKeyTab or VirtualKeyEscape or VirtualKeyF4 or VirtualKeySpace)
        {
            errorMessage = "该组合键由 Windows 保留，不能使用。";
            return true;
        }

        if ((Modifiers & HotKeyModifiers.Control) != 0 &&
            VirtualKey == VirtualKeyEscape)
        {
            errorMessage = "Ctrl+Esc 由 Windows 保留，不能使用。";
            return true;
        }

        if ((Modifiers & (HotKeyModifiers.Control | HotKeyModifiers.Alt)) ==
                (HotKeyModifiers.Control | HotKeyModifiers.Alt) &&
            VirtualKey == VirtualKeyDelete)
        {
            errorMessage = "Ctrl+Alt+Delete 由 Windows 保留，不能使用。";
            return true;
        }

        if (VirtualKey == VirtualKeyPrintScreen)
        {
            errorMessage = "PrintScreen 属于系统截图快捷键，不能使用。";
            return true;
        }

        errorMessage = string.Empty;
        return false;
    }

    private static bool TryParseModifier(string token, out HotKeyModifiers modifier)
    {
        modifier = token.ToUpperInvariant() switch
        {
            "CTRL" or "CONTROL" => HotKeyModifiers.Control,
            "ALT" => HotKeyModifiers.Alt,
            "SHIFT" => HotKeyModifiers.Shift,
            "WIN" or "WINDOWS" => HotKeyModifiers.Windows,
            _ => HotKeyModifiers.None,
        };

        return modifier != HotKeyModifiers.None;
    }

    private static bool TryParseKey(string token, out uint virtualKey)
    {
        var normalizedToken = token.Trim();

        if (normalizedToken.Length == 1)
        {
            var key = normalizedToken[0];

            if (char.IsAsciiLetter(key))
            {
                virtualKey = char.ToUpperInvariant(key);
                return true;
            }

            if (char.IsAsciiDigit(key))
            {
                virtualKey = key;
                return true;
            }

            if (key == ',')
            {
                virtualKey = VirtualKeyComma;
                return true;
            }
        }

        if (normalizedToken.StartsWith('F') &&
            int.TryParse(normalizedToken[1..], out var functionKeyNumber) &&
            functionKeyNumber is >= 1 and <= 24)
        {
            virtualKey = (uint)(0x70 + functionKeyNumber - 1);
            return true;
        }

        virtualKey = normalizedToken.ToUpperInvariant() switch
        {
            "TAB" => VirtualKeyTab,
            "ESC" or "ESCAPE" => VirtualKeyEscape,
            "SPACE" => VirtualKeySpace,
            "PAGEUP" => VirtualKeyPageUp,
            "PAGEDOWN" => VirtualKeyPageDown,
            "HOME" => VirtualKeyHome,
            "END" => VirtualKeyEnd,
            "LEFT" => VirtualKeyLeft,
            "UP" => VirtualKeyUp,
            "RIGHT" => VirtualKeyRight,
            "DOWN" => VirtualKeyDown,
            "PRINTSCREEN" or "PRTSC" => VirtualKeyPrintScreen,
            "INSERT" or "INS" => VirtualKeyInsert,
            "DELETE" or "DEL" => VirtualKeyDelete,
            "SEMICOLON" => VirtualKeySemicolon,
            "PLUS" => VirtualKeyPlus,
            "COMMA" => VirtualKeyComma,
            "MINUS" => VirtualKeyMinus,
            "PERIOD" => VirtualKeyPeriod,
            "SLASH" => VirtualKeySlash,
            "BACKTICK" => VirtualKeyBacktick,
            "LEFTBRACKET" => VirtualKeyLeftBracket,
            "BACKSLASH" => VirtualKeyBackslash,
            "RIGHTBRACKET" => VirtualKeyRightBracket,
            "QUOTE" => VirtualKeyQuote,
            _ => 0,
        };

        return virtualKey != 0;
    }

    private static string GetKeyName(uint virtualKey)
    {
        if (virtualKey is >= 'A' and <= 'Z' or >= '0' and <= '9')
        {
            return char.ConvertFromUtf32((int)virtualKey);
        }

        if (virtualKey is >= 0x70 and <= 0x87)
        {
            return $"F{virtualKey - 0x70 + 1}";
        }

        return virtualKey switch
        {
            VirtualKeyTab => "Tab",
            VirtualKeyEscape => "Esc",
            VirtualKeySpace => "Space",
            VirtualKeyPageUp => "PageUp",
            VirtualKeyPageDown => "PageDown",
            VirtualKeyEnd => "End",
            VirtualKeyHome => "Home",
            VirtualKeyLeft => "Left",
            VirtualKeyUp => "Up",
            VirtualKeyRight => "Right",
            VirtualKeyDown => "Down",
            VirtualKeyPrintScreen => "PrintScreen",
            VirtualKeyInsert => "Insert",
            VirtualKeyDelete => "Delete",
            VirtualKeySemicolon => "Semicolon",
            VirtualKeyPlus => "Plus",
            VirtualKeyComma => "Comma",
            VirtualKeyMinus => "Minus",
            VirtualKeyPeriod => "Period",
            VirtualKeySlash => "Slash",
            VirtualKeyBacktick => "Backtick",
            VirtualKeyLeftBracket => "LeftBracket",
            VirtualKeyBackslash => "Backslash",
            VirtualKeyRightBracket => "RightBracket",
            VirtualKeyQuote => "Quote",
            _ => $"VK_{virtualKey:X2}",
        };
    }
}
