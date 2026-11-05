using System.Windows.Input;

namespace Screenshot.App.Core;

internal static class CaptureToolbarShortcut
{
    public static bool TryNormalize(string? value, out string shortcut)
    {
        shortcut = string.Empty;
        value = value?.Trim() ?? string.Empty;
        if (value.Length == 0) return true;
        if (value.Length == 1 && value[0] is >= '0' and <= '9')
        {
            shortcut = value;
            return true;
        }
        if (!HotKeyGesture.TryParse(value, out var gesture, out _) ||
            gesture.IsSystemReserved(out _) ||
            gesture.VirtualKey is not (>= '0' and <= '9' or >= 0x60 and <= 0x69))
            return false;

        // The toolbar has always treated the number row and numpad alike.
        if (gesture.VirtualKey is >= 0x60 and <= 0x69)
            gesture = gesture with { VirtualKey = gesture.VirtualKey - 0x60 + '0' };
        shortcut = gesture.ToString();
        return true;
    }

    public static bool TryFind(
        IReadOnlyDictionary<CaptureToolbarFeature, string> shortcuts,
        Key key, ModifierKeys modifiers, out CaptureToolbarFeature feature)
    {
        feature = default;
        uint virtualKey;
        if (key is >= Key.D0 and <= Key.D9) virtualKey = (uint)('0' + (key - Key.D0));
        else if (key is >= Key.NumPad0 and <= Key.NumPad9) virtualKey = (uint)('0' + (key - Key.NumPad0));
        else return false;

        var hotKeyModifiers = HotKeyModifiers.None;
        if (modifiers.HasFlag(ModifierKeys.Control)) hotKeyModifiers |= HotKeyModifiers.Control;
        if (modifiers.HasFlag(ModifierKeys.Alt)) hotKeyModifiers |= HotKeyModifiers.Alt;
        if (modifiers.HasFlag(ModifierKeys.Shift)) hotKeyModifiers |= HotKeyModifiers.Shift;
        if (modifiers.HasFlag(ModifierKeys.Windows)) hotKeyModifiers |= HotKeyModifiers.Windows;
        var pressed = new HotKeyGesture(hotKeyModifiers, virtualKey).ToString();
        foreach (var pair in shortcuts)
        {
            if (TryNormalize(pair.Value, out var candidate) && candidate == pressed)
            {
                feature = pair.Key;
                return true;
            }
        }
        return false;
    }
}
