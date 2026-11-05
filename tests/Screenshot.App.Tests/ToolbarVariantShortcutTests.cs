using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Screenshot.App.Capture;
using Screenshot.App.Editor;
using Screenshot.App.Presentation;

namespace Screenshot.App.Tests;

public sealed class ToolbarVariantShortcutTests
{
    [Fact]
    public void HandledFeatureShortcutCancelsPendingShiftBeforeRelease()
    {
        WpfTestHost.Invoke(() =>
        {
            var host = new Border();
            var row = new ToolbarOptionsRow();
            var menu = new ContextMenu();
            var clicks = 0;
            for (var i = 0; i < 2; i++)
            {
                var item = new MenuItem { Header = "variant", IsCheckable = true, IsChecked = i == 0 };
                item.Click += (_, _) => clicks++;
                menu.Items.Add(item);
            }
            row.Show(new RadioButton { ContextMenu = menu }, () => null);
            host.PreviewKeyDown += (_, e) => { if (e.Key == Key.D1) e.Handled = true; };
            ToolbarVariantShortcut.Attach(host, () => row);
            using var source = new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters("Hidden variant keys")
                { WindowStyle = unchecked((int)0x80000000), Width = 10, Height = 10 });
            source.RootVisual = host;
            void Send(Key key, RoutedEvent routedEvent) => host.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = routedEvent });
            Send(Key.LeftShift, Keyboard.PreviewKeyDownEvent);
            Send(Key.D1, Keyboard.PreviewKeyDownEvent);
            Send(Key.LeftShift, Keyboard.PreviewKeyUpEvent);
            Assert.Equal(0, clicks);
            Send(Key.LeftShift, Keyboard.PreviewKeyDownEvent);
            Send(Key.LeftShift, Keyboard.PreviewKeyUpEvent);
            Assert.Equal(1, clicks);
        });
    }

    [Theory]
    [InlineData("InlineShapeToolButton", 2)]
    [InlineData("InlineArrowToolButton", 4)]
    [InlineData("InlineDoubleArrowToolButton", 2)]
    [InlineData("InlineLineToolButton", 2)]
    [InlineData("InlineMosaicToolButton", 2)]
    [InlineData("InlineTextToolButton", 2)]
    public void ShiftCyclesRealToolActionsAndIconsAndWraps(string buttonName, int count)
    {
        WpfTestHost.Invoke(() =>
        {
            var saved = AnnotationToolPreferences.Snapshot();
            using var window = new CaptureOverlayWindow(null, isStartupPrewarm: true, deferInitialScreenCapture: true);
            try
            {
                var button = (RadioButton)window.FindName(buttonName);
                button.IsChecked = true;
                var row = (ToolbarOptionsRow)window.FindName("InlineToolAdjustments");
                var shortcut = new ToolbarVariantShortcut();
                var choices = row.Children.OfType<ToggleButton>().ToArray();
                Assert.Equal(count, choices.Length);
                var initial = Array.FindIndex(choices, choice => choice.IsChecked == true);
                Assert.True(initial >= 0);
                var icons = new HashSet<string>();
                for (var step = 0; step < count; step++)
                {
                    icons.Add(Icon(window, buttonName, button));
                    shortcut.KeyDown(Key.LeftShift, ModifierKeys.Shift, false, row);
                    shortcut.KeyDown(Key.LeftShift, ModifierKeys.Shift, true, row);
                    Assert.True(choices[(initial + step) % count].IsChecked);
                    Assert.True(shortcut.KeyUp(Key.LeftShift, ModifierKeys.None, row));
                    Assert.True(choices[(initial + step + 1) % count].IsChecked);
                    Assert.False(shortcut.KeyUp(Key.LeftShift, ModifierKeys.None, row));
                }
                Assert.Equal(count, icons.Count);
                Assert.True(choices[initial].IsChecked);
                Assert.False(window.IsVisible);
            }
            finally { window.Close(); AnnotationToolPreferences.Configure(saved, null); }
        });
    }

    [Fact]
    public void ModifiedShiftMouseGestureAndChangedToolDoNotCycle()
    {
        WpfTestHost.Invoke(() =>
        {
            var row = new ToolbarOptionsRow();
            var menu = new ContextMenu();
            var selected = 0;
            for (var i = 0; i < 2; i++)
            {
                var index = i;
                var item = new MenuItem { Header = i.ToString(System.Globalization.CultureInfo.InvariantCulture), IsCheckable = true, IsChecked = i == 0 };
                item.Click += (_, _) => selected = index;
                menu.Items.Add(item);
            }
            row.Show(new RadioButton { ContextMenu = menu }, () => null);
            var shortcut = new ToolbarVariantShortcut();
            shortcut.KeyDown(Key.LeftShift, ModifierKeys.Control | ModifierKeys.Shift, false, row);
            Assert.False(shortcut.KeyUp(Key.LeftShift, ModifierKeys.None, row));
            shortcut.KeyDown(Key.LeftShift, ModifierKeys.Shift, false, row);
            shortcut.KeyDown(Key.D1, ModifierKeys.Shift, false, row);
            Assert.False(shortcut.KeyUp(Key.LeftShift, ModifierKeys.None, row));
            shortcut.KeyDown(Key.LeftShift, ModifierKeys.Shift, false, row);
            shortcut.Cancel(); // Mouse down / focus loss / deactivation.
            Assert.False(shortcut.KeyUp(Key.LeftShift, ModifierKeys.None, row));
            shortcut.KeyDown(Key.LeftShift, ModifierKeys.Shift, false, row);
            Assert.False(shortcut.KeyUp(Key.LeftShift, ModifierKeys.None, null));
            Assert.Equal(0, selected);
            shortcut.KeyDown(Key.RightShift, ModifierKeys.Shift, false, row);
            Assert.True(shortcut.KeyUp(Key.RightShift, ModifierKeys.None, row));
            Assert.Equal(1, selected);
            row.Clear();
            Assert.False(row.CycleChoice());
        });
    }

    private static string Icon(Window window, string name, RadioButton button) => name switch
    {
        "InlineShapeToolButton" => PathIcon((System.Windows.Shapes.Path)window.FindName("InlineShapeToolIcon")),
        "InlineArrowToolButton" => PathIcon((System.Windows.Shapes.Path)window.FindName("InlineArrowToolIcon")),
        "InlineTextToolButton" => ((Grid)button.Content).Tag.ToString()!,
        _ => PathIcon((System.Windows.Shapes.Path)button.Content),
    };

    private static string PathIcon(System.Windows.Shapes.Path path) =>
        $"{path.Data}:{path.Fill}:{path.Stroke}:{path.StrokeThickness}";
}
