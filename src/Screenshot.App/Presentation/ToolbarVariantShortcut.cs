using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace Screenshot.App.Presentation;

// Commit on release so Shift+click, drawing constraints and configured
// Shift+digit shortcuts cannot accidentally change the selected variant.
internal sealed class ToolbarVariantShortcut
{
    private ToolbarOptionsRow? _pending;
    private Key _shift;

    internal static Action Attach(UIElement host, Func<ToolbarOptionsRow?> getOptions)
    {
        var shortcut = new ToolbarVariantShortcut();
        bool CanCycle() => System.Windows.Input.Mouse.LeftButton == MouseButtonState.Released &&
            System.Windows.Input.Mouse.RightButton == MouseButtonState.Released &&
            System.Windows.Input.Mouse.MiddleButton == MouseButtonState.Released &&
            !IsEditingInput(Keyboard.FocusedElement as DependencyObject);
        // Observe handled digit shortcuts too: the host may execute them
        // before this handler, but they must still cancel a pending Shift tap.
        System.Windows.Input.KeyEventHandler down = (_, e) =>
            shortcut.KeyDown(e.Key, Keyboard.Modifiers, e.IsRepeat, CanCycle() ? getOptions() : null);
        System.Windows.Input.KeyEventHandler up = (_, e) =>
        {
            if (shortcut.KeyUp(e.Key, Keyboard.Modifiers, CanCycle() ? getOptions() : null)) e.Handled = true;
        };
        MouseButtonEventHandler mouse = (_, _) => shortcut.Cancel();
        MouseWheelEventHandler wheel = (_, _) => shortcut.Cancel();
        KeyboardFocusChangedEventHandler focus = (_, _) => shortcut.Cancel();
        EventHandler deactivated = (_, _) => shortcut.Cancel();
        host.AddHandler(Keyboard.PreviewKeyDownEvent, down, true);
        host.PreviewKeyUp += up;
        host.AddHandler(UIElement.PreviewMouseDownEvent, mouse, true);
        host.AddHandler(UIElement.PreviewMouseWheelEvent, wheel, true);
        host.AddHandler(Keyboard.LostKeyboardFocusEvent, focus, true);
        if (host is Window window) window.Deactivated += deactivated;
        return () =>
        {
            shortcut.Cancel();
            host.RemoveHandler(Keyboard.PreviewKeyDownEvent, down);
            host.PreviewKeyUp -= up;
            host.RemoveHandler(UIElement.PreviewMouseDownEvent, mouse);
            host.RemoveHandler(UIElement.PreviewMouseWheelEvent, wheel);
            host.RemoveHandler(Keyboard.LostKeyboardFocusEvent, focus);
            if (host is Window owner) owner.Deactivated -= deactivated;
        };
    }

    private static bool IsEditingInput(DependencyObject? element)
    {
        while (element is not null)
        {
            if (element is System.Windows.Controls.Primitives.TextBoxBase or Selector or PasswordBox) return true;
            element = element is FrameworkElement framework ? framework.Parent : null;
        }
        return false;
    }

    internal void KeyDown(Key key, ModifierKeys modifiers, bool repeat, ToolbarOptionsRow? options)
    {
        if (repeat) return;
        Cancel();
        if (key is not (Key.LeftShift or Key.RightShift) ||
            (modifiers & ~ModifierKeys.Shift) != ModifierKeys.None) return;
        _pending = options;
        _shift = key;
    }

    internal bool KeyUp(Key key, ModifierKeys modifiers, ToolbarOptionsRow? options)
    {
        var pending = _pending;
        Cancel();
        return key == _shift && modifiers == ModifierKeys.None && pending is not null &&
            ReferenceEquals(pending, options) && pending.CycleChoice();
    }

    internal void Cancel() => _pending = null;
}
