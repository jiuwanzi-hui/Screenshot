using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using SnapCut.Mac.Native;
using SnapCut.Mac.Text;

namespace SnapCut.Mac.Presentation;

internal sealed class TranslationResultWindow : Window
{
    public TranslationResultWindow(
        string source,
        MacTranslationResult result)
    {
        Title = "SnapCut 翻译";
        Width = 680;
        Height = 560;
        MinWidth = 480;
        MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(MacTheme.WindowBackground);
        var sourceBox = CreateBox(source);
        var targetText = result.IsSuccess ? result.Text : result.ErrorMessage ?? "翻译失败";
        var targetBox = CreateBox(targetText);
        var status = new TextBlock
        {
            Text = result.IsSuccess ? "翻译完成" : "翻译失败",
            Foreground = new SolidColorBrush(MacTheme.SecondaryText),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var copy = MacTheme.CreateButton("复制译文", primary: true);
        copy.Click += (_, _) => status.Text = MacNativeUi.CopyText(targetText)
            ? "译文已复制"
            : "复制失败";
        var close = MacTheme.CreateButton("关闭");
        close.Click += (_, _) => Close();
        var toolbar = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"),
            ColumnSpacing = 8,
            Children = { status, copy, close },
        };
        Grid.SetColumn(copy, 1);
        Grid.SetColumn(close, 2);
        var labels = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*"),
            ColumnSpacing = 12,
            Children =
            {
                new TextBlock { Text = "原文", FontWeight = FontWeight.SemiBold },
                new TextBlock { Text = "译文", FontWeight = FontWeight.SemiBold },
            },
        };
        Grid.SetColumn(labels.Children[1], 1);
        var boxes = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*"),
            ColumnSpacing = 12,
            Children = { sourceBox, targetBox },
        };
        Grid.SetColumn(targetBox, 1);
        Content = new Grid
        {
            Margin = new Thickness(18),
            RowDefinitions = new RowDefinitions("Auto,*,Auto"),
            RowSpacing = 8,
            Children = { labels, boxes, toolbar },
        };
        Grid.SetRow(boxes, 1);
        Grid.SetRow(toolbar, 2);
        Opened += (_, _) => MacNativeUi.ExcludeFromScreenCapture(this);
    }

    private static TextBox CreateBox(string text) => new()
    {
        Text = text,
        IsReadOnly = true,
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        VerticalContentAlignment = VerticalAlignment.Top,
        Padding = new Thickness(10),
    };
}
