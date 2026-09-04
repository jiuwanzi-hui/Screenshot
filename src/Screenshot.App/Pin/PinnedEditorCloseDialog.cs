using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WpfButton = System.Windows.Controls.Button;
using WpfKeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace Screenshot.App.Pin;

internal enum PinnedEditorCloseChoice
{
    ContinueEditing,
    Save,
    Discard,
}

internal sealed class PinnedEditorCloseDialog : Window
{
    public PinnedEditorCloseDialog(Window owner, string title)
    {
        Owner = owner;
        Title = title;
        Width = 410;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = System.Windows.Media.Brushes.Transparent;
        FontFamily = new System.Windows.Media.FontFamily("Microsoft YaHei UI");
        Topmost = owner.Topmost;
        PreviewKeyDown += OnPreviewKeyDown;

        var shell = new Border
        {
            Padding = new Thickness(22),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
        };
        shell.SetResourceReference(Border.BackgroundProperty, "AppPanelBackgroundBrush");
        shell.SetResourceReference(Border.BorderBrushProperty, "AppBorderBrush");

        var content = new StackPanel();
        var heading = new TextBlock
        {
            Text = "工具栏仍处于编辑状态",
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
        };
        heading.SetResourceReference(TextBlock.ForegroundProperty, "AppTextPrimaryBrush");
        content.Children.Add(heading);
        var message = new TextBlock
        {
            Margin = new Thickness(0, 9, 0, 20),
            Text = "是否保存当前标注？也可以放弃修改或返回继续编辑。",
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
        };
        message.SetResourceReference(TextBlock.ForegroundProperty, "AppTextSecondaryBrush");
        content.Children.Add(message);

        var buttons = new StackPanel
        {
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Orientation = System.Windows.Controls.Orientation.Horizontal,
        };
        buttons.Children.Add(CreateButton("继续编辑", PinnedEditorCloseChoice.ContinueEditing, isCancel: true));
        buttons.Children.Add(CreateButton("放弃修改", PinnedEditorCloseChoice.Discard));
        var save = CreateButton("保存", PinnedEditorCloseChoice.Save, isDefault: true);
        save.SetResourceReference(WpfButton.BackgroundProperty, "EditorToolbarConfirmBackgroundBrush");
        save.SetResourceReference(WpfButton.BorderBrushProperty, "EditorToolbarConfirmBorderBrush");
        buttons.Children.Add(save);
        content.Children.Add(buttons);
        shell.Child = content;
        Content = shell;
    }

    public PinnedEditorCloseChoice Choice { get; private set; } = PinnedEditorCloseChoice.ContinueEditing;

    private WpfButton CreateButton(string label, PinnedEditorCloseChoice choice, bool isDefault = false, bool isCancel = false)
    {
        var button = new WpfButton
        {
            Content = label,
            Width = 92,
            Height = 34,
            Margin = new Thickness(8, 0, 0, 0),
            IsDefault = isDefault,
            IsCancel = isCancel,
        };
        button.Click += (_, _) =>
        {
            Choice = choice;
            Close();
        };
        return button;
    }

    private void OnPreviewKeyDown(object sender, WpfKeyEventArgs e)
    {
        if (e.Key != Key.Escape)
        {
            return;
        }

        e.Handled = true;
        Choice = PinnedEditorCloseChoice.ContinueEditing;
        Close();
    }
}
