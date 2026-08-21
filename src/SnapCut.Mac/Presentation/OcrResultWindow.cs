using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using SnapCut.Mac.Native;
using SnapCut.Mac.Text;

namespace SnapCut.Mac.Presentation;

internal sealed class OcrResultWindow : Window
{
    public OcrResultWindow(MacOcrRecognitionResult result)
    {
        Title = "SnapCut 文字识别";
        Width = 640;
        Height = 520;
        MinWidth = 460;
        MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(MacTheme.WindowBackground);
        var text = new TextBox
        {
            Text = result.IsSuccess ? result.Text : result.ErrorMessage,
            AcceptsReturn = true,
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 14,
            Padding = new Thickness(12),
            VerticalContentAlignment = VerticalAlignment.Top,
        };
        var copy = MacTheme.CreateButton("复制全部", primary: true);
        var status = new TextBlock
        {
            Text = result.IsSuccess
                ? $"识别到 {result.Regions.Count} 个文本区域"
                : "识别失败",
            Foreground = new SolidColorBrush(MacTheme.SecondaryText),
            VerticalAlignment = VerticalAlignment.Center,
        };
        copy.Click += (_, _) =>
        {
            status.Text = MacNativeUi.CopyText(text.Text ?? string.Empty)
                ? "已复制全部文字"
                : "复制失败";
        };
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
        Content = new Grid
        {
            Margin = new Thickness(18),
            RowDefinitions = new RowDefinitions("*,Auto"),
            RowSpacing = 12,
            Children = { text, toolbar },
        };
        Grid.SetRow(toolbar, 1);
        Opened += (_, _) => MacNativeUi.ExcludeFromScreenCapture(this);
    }
}
