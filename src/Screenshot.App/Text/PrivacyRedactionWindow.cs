using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using WpfBinding = System.Windows.Data.Binding;
using WpfButton = System.Windows.Controls.Button;
using WpfCheckBox = System.Windows.Controls.CheckBox;
using WpfControl = System.Windows.Controls.Control;
using WpfFontFamily = System.Windows.Media.FontFamily;
using WpfListBox = System.Windows.Controls.ListBox;
using WpfOrientation = System.Windows.Controls.Orientation;

namespace Screenshot.App.Text;

public sealed class PrivacyRedactionItem(PrivacyCandidate candidate)
{
    public PrivacyCandidate Candidate { get; } = candidate;
    public bool IsSelected { get; set; } = true;
}

public sealed class PrivacyRedactionWindow : Window
{
    private readonly IReadOnlyList<PrivacyRedactionItem> _items;

    public PrivacyRedactionWindow(IReadOnlyList<PrivacyCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        _items = candidates.Select(candidate =>
            new PrivacyRedactionItem(candidate)).ToArray();

        Title = "确认隐私打码";
        Width = 560;
        Height = Math.Min(620, 220 + (_items.Count * 45));
        MinWidth = 460;
        MinHeight = 300;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.CanResize;
        ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "AppBackgroundBrush");
        Content = BuildContent();
    }

    public IReadOnlyList<PrivacyCandidate> SelectedCandidates => _items
        .Where(item => item.IsSelected)
        .Select(item => item.Candidate)
        .ToArray();

    private Grid BuildContent()
    {
        var root = new Grid { Margin = new Thickness(22) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var title = new TextBlock
        {
            Text = $"检测到 {_items.Count} 项可能的敏感信息",
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
        };
        title.SetResourceReference(TextBlock.ForegroundProperty,
            "AppTextPrimaryBrush");
        root.Children.Add(title);

        var hint = new TextBlock
        {
            Text = "请核对候选项。取消勾选误识别内容后再批量打码。",
            Margin = new Thickness(0, 7, 0, 16),
            TextWrapping = TextWrapping.Wrap,
        };
        hint.SetResourceReference(TextBlock.ForegroundProperty,
            "AppTextSecondaryBrush");
        Grid.SetRow(hint, 1);
        root.Children.Add(hint);

        var list = new WpfListBox
        {
            ItemsSource = _items,
            BorderThickness = new Thickness(1),
        };
        list.SetResourceReference(WpfControl.BackgroundProperty,
            "AppPanelBackgroundBrush");
        list.SetResourceReference(WpfControl.BorderBrushProperty,
            "AppBorderBrush");
        var template = new DataTemplate(typeof(PrivacyRedactionItem));
        var check = new FrameworkElementFactory(typeof(WpfCheckBox));
        check.SetBinding(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty,
            new WpfBinding(nameof(PrivacyRedactionItem.IsSelected))
            {
                Mode = BindingMode.TwoWay,
            });
        check.SetValue(FrameworkElement.MarginProperty, new Thickness(8, 7, 8, 7));
        var panel = new FrameworkElementFactory(typeof(StackPanel));
        panel.SetValue(StackPanel.OrientationProperty, WpfOrientation.Horizontal);
        var kind = new FrameworkElementFactory(typeof(TextBlock));
        kind.SetValue(FrameworkElement.WidthProperty, 104d);
        kind.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        kind.SetBinding(TextBlock.TextProperty,
            new WpfBinding("Candidate.KindLabel"));
        var value = new FrameworkElementFactory(typeof(TextBlock));
        value.SetValue(TextBlock.FontFamilyProperty,
            new WpfFontFamily("Consolas"));
        value.SetBinding(TextBlock.TextProperty,
            new WpfBinding("Candidate.MaskedValue"));
        panel.AppendChild(kind);
        panel.AppendChild(value);
        check.AppendChild(panel);
        template.VisualTree = check;
        list.ItemTemplate = template;
        Grid.SetRow(list, 2);
        root.Children.Add(list);

        var actions = new StackPanel
        {
            Margin = new Thickness(0, 16, 0, 0),
            Orientation = WpfOrientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
        };
        var cancel = new WpfButton
        {
            Content = "取消",
            MinWidth = 82,
            Height = 34,
            Margin = new Thickness(0, 0, 10, 0),
            IsCancel = true,
        };
        var apply = new WpfButton
        {
            Content = "打码所选项",
            MinWidth = 112,
            Height = 34,
            IsDefault = true,
        };
        apply.Click += (_, _) =>
        {
            DialogResult = true;
            Close();
        };
        actions.Children.Add(cancel);
        actions.Children.Add(apply);
        Grid.SetRow(actions, 3);
        root.Children.Add(actions);
        return root;
    }
}
