using System.IO;
using System.Runtime.InteropServices;
using WpfClipboard = System.Windows.Clipboard;

namespace Screenshot.App.Text;

internal static class SelectedTextCaptureService
{
    private const int VirtualKeyControl = 0x11;
    private const int VirtualKeyMenu = 0x12;
    private const int VirtualKeyShift = 0x10;
    private const int VirtualKeyLeftWindows = 0x5B;
    private const int VirtualKeyRightWindows = 0x5C;
    private const byte VirtualKeyC = 0x43;
    private const uint KeyEventKeyUp = 0x0002;

    public static async Task<string?> TryCopySelectedTextAsync(
        CancellationToken cancellationToken = default)
    {
        await WaitForShortcutModifiersToReleaseAsync(cancellationToken);
        var previousSequence = NativeMethods.GetClipboardSequenceNumber();
        NativeMethods.keybd_event(VirtualKeyControl, 0, 0, UIntPtr.Zero);
        NativeMethods.keybd_event(VirtualKeyC, 0, 0, UIntPtr.Zero);
        NativeMethods.keybd_event(VirtualKeyC, 0, KeyEventKeyUp, UIntPtr.Zero);
        NativeMethods.keybd_event(
            VirtualKeyControl,
            0,
            KeyEventKeyUp,
            UIntPtr.Zero);

        for (var attempt = 0; attempt < 12; attempt++)
        {
            await Task.Delay(35, cancellationToken);
            if (NativeMethods.GetClipboardSequenceNumber() == previousSequence)
            {
                continue;
            }

            try
            {
                if (WpfClipboard.ContainsText())
                {
                    var text = WpfClipboard.GetText().Trim();
                    return text.Length == 0 ? null : text;
                }

                if (WpfClipboard.ContainsFileDropList())
                {
                    var names = WpfClipboard.GetFileDropList()
                        .Cast<string>()
                        .Select(Path.GetFileName)
                        .Where(name => !string.IsNullOrWhiteSpace(name));
                    var fileNames = string.Join(Environment.NewLine, names);
                    return fileNames.Length == 0 ? null : fileNames;
                }
            }
            catch (COMException)
            {
            }
        }

        return null;
    }

    private static async Task WaitForShortcutModifiersToReleaseAsync(
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            if (!IsPressed(VirtualKeyControl) &&
                !IsPressed(VirtualKeyMenu) &&
                !IsPressed(VirtualKeyShift) &&
                !IsPressed(VirtualKeyLeftWindows) &&
                !IsPressed(VirtualKeyRightWindows))
            {
                return;
            }

            await Task.Delay(20, cancellationToken);
        }
    }

    private static bool IsPressed(int virtualKey)
    {
        return (NativeMethods.GetAsyncKeyState(virtualKey) & 0x8000) != 0;
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll")]
        public static extern short GetAsyncKeyState(int virtualKey);

        [DllImport("user32.dll")]
        public static extern uint GetClipboardSequenceNumber();

        [DllImport("user32.dll")]
        public static extern void keybd_event(
            byte virtualKey,
            byte scanCode,
            uint flags,
            UIntPtr extraInformation);
    }
}
