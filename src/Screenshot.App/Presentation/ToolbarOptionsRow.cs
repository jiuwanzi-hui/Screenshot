using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Screenshot.App.Editor;
using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using Control = System.Windows.Controls.Control;
using MenuItem = System.Windows.Controls.MenuItem;
using Panel = System.Windows.Controls.Panel;
using Orientation = System.Windows.Controls.Orientation;
using TextBlock = System.Windows.Controls.TextBlock;
using WrapPanel = System.Windows.Controls.WrapPanel;

namespace Screenshot.App.Presentation;

// A single, lazily populated options row. Menu actions remain the source of
// truth, so shortcuts, right-click menus and inline choices use the same code.
public sealed class ToolbarOptionsRow : WrapPanel
{
    public static readonly DependencyProperty UsesInlineOptionsProperty = DependencyProperty.RegisterAttached(
        "UsesInlineOptions", typeof(bool), typeof(ToolbarOptionsRow), new PropertyMetadata(false));
    public static bool GetUsesInlineOptions(DependencyObject element) => (bool)element.GetValue(UsesInlineOptionsProperty);
    public static void SetUsesInlineOptions(DependencyObject element, bool value) => element.SetValue(UsesInlineOptionsProperty, value);

    private ButtonBase? _button;
    private object? _contentKey;
    internal event Action? OptionsChanged;

    public ToolbarOptionsRow()
    {
        Orientation = Orientation.Horizontal;
        Visibility = Visibility.Collapsed;
        Margin = new Thickness(0, 6, 0, 0);
    }

    internal void Clear()
    {
        Children.Clear();
        _button = null;
        _contentKey = null;
        MaxWidth = 600;
        ToolbarAdjustmentPanel.SetFillRemainingWidth(this, false);
        Visibility = Visibility.Collapsed;
    }

    internal void Show(ButtonBase button, Func<ImageEditorCanvas?> canvas, bool number = false)
    {
        var key = number ? typeof(NumberAnnotationOptions) : (object?)button.ContextMenu;
        if (ReferenceEquals(_button, button) && ReferenceEquals(_contentKey, key))
        {
            RefreshChecks();
            if (Children.Count > 0 && Children[0] is FrameworkElement { Tag: Action refresh }) refresh();
            return;
        }
        Clear();
        _button = button;
        _contentKey = key;
        if (number)
            Children.Add(NumberAnnotationOptions.CreateInline(canvas));
        else if (button.ContextMenu is { } menu)
        {
            foreach (var item in menu.Items.OfType<MenuItem>())
            {
                var choice = new ToggleButton
                {
                    Content = GetHeaderText(item.Header), Tag = item,
                    MinHeight = 28, MinWidth = 52, Padding = new Thickness(10, 3, 10, 3),
                    Margin = new Thickness(0, 0, 5, 0), Focusable = false,
                    ToolTip = item.ToolTip ?? GetHeaderText(item.Header),
                };
                choice.SetResourceReference(Control.ForegroundProperty, "EditorToolbarIconBrush");
                choice.SetResourceReference(Control.BackgroundProperty, "EditorToolbarButtonBackgroundBrush");
                choice.SetResourceReference(Control.BorderBrushProperty, "EditorToolbarButtonBorderBrush");
                choice.SetResourceReference(FrameworkElement.StyleProperty, "ToolbarInlineChoiceStyle");
                choice.Click += (_, _) =>
                {
                    item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, item));
                    RefreshChecks();
                    OptionsChanged?.Invoke();
                };
                Children.Add(choice);
            }
        }
        RefreshChecks();
        Visibility = Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RefreshChecks()
    {
        _button?.ContextMenu?.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent));
        foreach (var choice in Children.OfType<ToggleButton>())
            if (choice.Tag is MenuItem item)
            {
                choice.IsChecked = item.IsChecked;
                choice.IsEnabled = item.IsEnabled;
            }
    }

    internal bool CycleChoice()
    {
        if (Visibility != Visibility.Visible || !IsEnabled) return false;
        RefreshChecks();
        var choices = Children.OfType<ToggleButton>()
            .Where(choice => choice.IsEnabled && choice.Visibility == Visibility.Visible &&
                choice.Tag is MenuItem { IsCheckable: true }).ToArray();
        if (choices.Length < 2) return false;
        var selected = Array.FindIndex(choices, choice => choice.IsChecked == true);
        choices[(selected + 1) % choices.Length].RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        return true;
    }

    internal void ShowEmoji(Action<string> select)
    {
        if (_contentKey is string key && key == "emoji") return;
        Clear();
        _contentKey = "emoji";
        ToolbarAdjustmentPanel.SetFillRemainingWidth(this, true);
        var palette = new WrapPanel { ItemWidth = 34, ItemHeight = 34 };
        foreach (var emoji in EmojiStickerCatalog.All)
        {
            var button = new ToggleButton
            {
                Width = 30, Height = 30, Margin = new Thickness(2), Padding = new Thickness(2),
                ToolTip = emoji, Content = new EmojiStickerImage { Width = 23, Height = 23, Sticker = emoji },
            };
            button.Click += (_, _) => select(emoji);
            button.SetResourceReference(FrameworkElement.StyleProperty, "ToolbarInlineChoiceStyle");
            button.SetResourceReference(Control.BackgroundProperty, "EditorToolbarButtonBackgroundBrush");
            button.SetResourceReference(Control.BorderBrushProperty, "EditorToolbarButtonBorderBrush");
            button.Click += (_, _) => button.IsChecked = false;
            palette.Children.Add(button);
        }
        Children.Add(new ToolbarEmojiStrip { Content = palette });
        Visibility = Visibility.Visible;
    }

    private static string GetHeaderText(object? header) => header switch
    {
        string text => UiLocalization.Get(text),
        TextBlock text => text.Text,
        Panel panel => string.Join(" ", panel.Children.OfType<TextBlock>().Select(text => text.Text)),
        _ => header?.ToString() ?? string.Empty,
    };
}
