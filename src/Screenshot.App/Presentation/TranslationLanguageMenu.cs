using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Screenshot.App.Text;
using Button = System.Windows.Controls.Button;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace Screenshot.App.Presentation;

/// <summary>Keeps the button action independent from the shared target-language menu.</summary>
public static class TranslationLanguageMenu
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(TranslationLanguageMenu), new PropertyMetadata(false, OnEnabled));
    public static bool GetEnabled(DependencyObject value) => (bool)value.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject value, bool enabled) => value.SetValue(EnabledProperty, enabled);

    public static readonly DependencyProperty InlineOptionsProperty = DependencyProperty.RegisterAttached(
        "InlineOptions", typeof(bool), typeof(TranslationLanguageMenu), new PropertyMetadata(false));
    public static bool GetInlineOptions(DependencyObject value) => (bool)value.GetValue(InlineOptionsProperty);
    public static void SetInlineOptions(DependencyObject value, bool enabled) => value.SetValue(InlineOptionsProperty, enabled);

    private static void OnEnabled(DependencyObject source, DependencyPropertyChangedEventArgs args)
    {
        if (source is not Button button || args.NewValue is not true) return;
        button.ContextMenu = CreateMenu(deferItems: GetInlineOptions(button));
        button.ContextMenuOpening += (_, _) => EnsureItems(button.ContextMenu);
        button.ContextMenu.Closed += (_, _) => button.Focus();
        button.Loaded += OnLoaded;
        button.PreviewKeyDown += (_, e) =>
        {
            if (!(e.SystemKey == Key.Down && Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))) return;
            e.Handled = true;
            Open(button);
        };
    }

    private static void OnLoaded(object sender, RoutedEventArgs args)
    {
        var button = (Button)sender;
        button.Loaded -= OnLoaded;
        if (GetInlineOptions(button)) return;
        var label = button.Content;
        button.Content = null;
        var panel = new Grid();
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        panel.Children.Add(new ContentPresenter { Content = label, VerticalAlignment = VerticalAlignment.Center });
        var arrow = new ToolbarDropDownButton { FontSize = 9,
            VerticalAlignment = VerticalAlignment.Center, ToolTip = "选择目标语言（同步所有翻译入口）" };
        arrow.Click += (_, e) => { Open(button); e.Handled = true; };
        Grid.SetColumn(arrow, 1);
        panel.Children.Add(arrow);
        button.Content = panel;
        button.MinWidth = Math.Max(button.MinWidth, 42);
        button.Padding = new Thickness(0);
    }

    internal static ContextMenu CreateMenu(bool deferItems = false)
    {
        var menu = new ContextMenu { MaxHeight = 440 };
        if (deferItems)
            menu.Opened += (_, _) => { EnsureItems(menu); RefreshSelection(menu); };
        else PopulateMenu(menu);
        return menu;
    }

    private static void EnsureItems(ContextMenu menu)
    {
        if (menu.Items.Count == 0) PopulateMenu(menu);
    }

    private static void PopulateMenu(ContextMenu menu)
    {
        // Hovering a partly clipped item focuses it in WPF. Do not let that
        // focus move the list under the pointer; keyboard navigation may scroll.
        var keyboardNavigation = false;
        menu.PreviewMouseMove += (_, _) => keyboardNavigation = false;
        menu.PreviewKeyDown += (_, _) => keyboardNavigation = true;
        RequestBringIntoViewEventHandler suppressHoverScroll = (_, e) =>
        {
            if (!keyboardNavigation) e.Handled = true;
        };
        menu.SetResourceReference(FrameworkElement.StyleProperty, "ThemedContextMenuStyle");
        var heading = new MenuItem { Header = "翻译目标语言 · 全局同步", IsEnabled = false };
        heading.SetResourceReference(FrameworkElement.StyleProperty, "ThemedMenuItemStyle");
        menu.Items.Add(heading);
        var separator = new Separator();
        separator.SetResourceReference(FrameworkElement.StyleProperty, "ThemedMenuSeparatorStyle");
        menu.Items.Add(separator);
        foreach (var language in TranslationLanguageCatalog.Languages)
        {
            var item = new MenuItem { Header = $"{language.DisplayName}（{language.Tag}）", IsCheckable = true,
                IsChecked = language.Tag == (TranslationTargetPreferences.Language ?? "zh-Hans"), Tag = language.Tag };
            // Intercept at the item before this bubbling request reaches the viewer.
            item.AddHandler(FrameworkElement.RequestBringIntoViewEvent, suppressHoverScroll);
            item.SetResourceReference(FrameworkElement.StyleProperty, "ThemedMenuItemStyle");
            item.Click += (_, _) => TranslationTargetPreferences.Select(language.Tag);
            menu.Items.Add(item);
        }
        menu.Opened += (_, _) => RefreshSelection(menu);
    }

    private static void RefreshSelection(ContextMenu menu)
    {
        foreach (var item in menu.Items.OfType<MenuItem>().Where(item => item.IsCheckable))
            item.IsChecked = item.Tag as string == (TranslationTargetPreferences.Language ?? "zh-Hans");
    }

    private static void Open(Button button)
    {
        var menu = button.ContextMenu ?? CreateMenu();
        EnsureItems(menu);
        RefreshSelection(menu);
        menu.PlacementTarget = button;
        menu.Placement = PlacementMode.Bottom;
        button.ContextMenu = menu;
        menu.IsOpen = true;
    }
}
