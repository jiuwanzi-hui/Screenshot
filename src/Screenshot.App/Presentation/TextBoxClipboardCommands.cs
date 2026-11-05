using System.Windows;
using TextBox = System.Windows.Controls.TextBox;
using System.Windows.Input;
using Screenshot.App.Capture;

namespace Screenshot.App.Presentation;

// TextBox's built-in clipboard commands can synchronously enter OLE/another
// process (clipboard managers, remote desktops, VM clipboard integration).
// Keep Copy off the input dispatcher; password and rich-text controls retain
// their own command semantics.
internal static class TextBoxClipboardCommands
{
    private static bool _installed;
    internal static void Install()
    {
        if (_installed) return;
        _installed = true;
        EventManager.RegisterClassHandler(typeof(Window), CommandManager.PreviewCanExecuteEvent,
            new CanExecuteRoutedEventHandler(OnCanExecute));
        EventManager.RegisterClassHandler(typeof(Window), CommandManager.PreviewExecutedEvent,
            new ExecutedRoutedEventHandler(OnExecuted));
    }

    private static TextBox? FindBox(object source) => source as TextBox ?? Keyboard.FocusedElement as TextBox;
    private static bool IsClipboard(ICommand command) => command == ApplicationCommands.Copy;

    private static void OnCanExecute(object sender, CanExecuteRoutedEventArgs e)
    {
        if (!IsClipboard(e.Command) || FindBox(e.OriginalSource) is not { } box ||
            !ReferenceEquals(Window.GetWindow(box), sender)) return;
        e.CanExecute = box.IsEnabled && box.SelectionLength > 0;
        e.Handled = true;
    }

    private static async void OnExecuted(object sender, ExecutedRoutedEventArgs e)
    {
        if (!IsClipboard(e.Command) || FindBox(e.OriginalSource) is not { } box ||
            !ReferenceEquals(Window.GetWindow(box), sender)) return;
        e.Handled = true;
        await CopyAsync(box,
            text => ClipboardTextService.SetTextAsync(text),
            message =>
            {
                if (sender is MainWindow main) main.ShowStatus(message);
                else if (sender is TranslationProfileEditorWindow profile) profile.ShowClipboardStatus(message);
            });
    }

    internal static async Task CopyAsync(TextBox box, Func<string, Task> write, Action<string> report)
    {
        var text = box.SelectedText;
        if (text.Length == 0) return;
        var retired = false;
        RoutedEventHandler unloaded = (_, _) => retired = true;
        box.Unloaded += unloaded;
        try
        {
            await write(text);
        }
        catch (Exception)
        {
            if (!retired) report("剪贴板暂时无法访问，请稍后重试。原输入内容已保留。");
        }
        finally
        {
            box.Unloaded -= unloaded;
        }
    }
}
