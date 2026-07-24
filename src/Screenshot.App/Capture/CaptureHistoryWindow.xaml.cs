using System.Windows;

namespace Screenshot.App.Capture;

public partial class CaptureHistoryWindow : Window
{
    private readonly string _saveDirectory;

    public CaptureHistoryWindow(
        CaptureHistoryService historyService,
        string? saveDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(historyService);

        InitializeComponent();
        DataContext = historyService;
        _saveDirectory = string.IsNullOrWhiteSpace(saveDirectory)
            ? Core.AppMetadata.DefaultCaptureDirectory
            : saveDirectory;
    }

    private void OnViewHistoryItemClick(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button
            {
                Tag: CaptureHistoryItem item,
            })
        {
            return;
        }

        try
        {
            var preview = new CapturePreviewWindow(
                item.CreateCapturedImage(),
                _saveDirectory,
                item);
            preview.ConfigureForHistoryView();
            preview.Show();
            HistoryStatusText.Text = "已打开完整截图。";
        }
        catch
        {
            HistoryStatusText.Text = "无法打开这张历史截图。";
        }
    }

    private async void OnCopyHistoryItemClick(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button
            {
                Tag: CaptureHistoryItem item,
            })
        {
            return;
        }

        try
        {
            using var image = item.CreateCapturedImage();
            await ClipboardImageService.SetImageAsync(image.Preview);
            item.MarkCopied();
            HistoryStatusText.Text = "已复制完整截图到剪贴板。";
        }
        catch
        {
            HistoryStatusText.Text = "复制失败，剪贴板可能正被其他程序使用。";
        }
    }
}
