using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Screenshot.App.Text;
using Button = System.Windows.Controls.Button;
using ProgressBar = System.Windows.Controls.ProgressBar;
using Orientation = System.Windows.Controls.Orientation;

namespace Screenshot.App.Presentation;

public partial class MainWindow
{
    private TextBlock? _visualOcrStatus;
    private Button? _visualOcrDownload;
    private CancellationTokenSource? _visualOcrDownloadCancellation;

    private Border CreateVisualOcrModelCard()
    {
        var panel = new StackPanel();
        TextBlock Label(string text, bool heading = false)
        {
            var label = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, heading ? 0 : 8, 0, 0), FontSize = heading ? 15 : 12,
                FontWeight = heading ? FontWeights.SemiBold : FontWeights.Normal };
            label.SetResourceReference(TextBlock.ForegroundProperty, heading ? "AppTextPrimaryBrush" : "AppTextSecondaryBrush");
            panel.Children.Add(label);
            return label;
        }
        Label("Qwen3-VL 4B · 视觉识别", true);
        Label("适合艺术字、描边大字、横竖混排和复杂图片。独立可选，不改变当前识别方式。完全本机运行，不上传截图；模型也可能漏字或误读，结果仍需核对。文字选择以行框为单位。");
        Label("模型文件约 3.1 GiB，文件大小不等于运行内存。显卡运行建议预留 5 GiB 可用内存，CPU 运行建议 8 GiB；按当前显卡、显存和 CPU 负载适配，显卡不可用时回退 CPU，低配电脑会较慢。仅识别时加载，连续操作短暂复用，空闲约 10 秒、取消或退出后释放；关闭自动识别后仍可手动使用。单次选区最多 419 万像素，大图需分区识别。");
        _visualOcrStatus = Label("");
        Label($"安装目录：{VisualOcrModelManager.Shared.InstallationDirectory}");
        var progress = new ProgressBar { Height = 6, Margin = new Thickness(0, 10, 0, 0), Maximum = 100, Visibility = Visibility.Collapsed };
        panel.Children.Add(progress);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        _visualOcrDownload = new Button { Content = "下载视觉模型", Padding = new Thickness(14, 5, 14, 5) };
        _visualOcrDownload.SetResourceReference(StyleProperty, "PrimaryButton");
        var cancel = new Button { Content = "暂停下载", Margin = new Thickness(8, 0, 0, 0), Visibility = Visibility.Collapsed };
        cancel.SetResourceReference(StyleProperty, "SecondaryButton");
        cancel.Click += (_, _) => _visualOcrDownloadCancellation?.Cancel();
        _visualOcrDownload.Click += async (_, _) =>
        {
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_updateCancellationSource.Token);
            _visualOcrDownloadCancellation = cancellation;
            _visualOcrDownload.IsEnabled = false;
            cancel.Visibility = progress.Visibility = Visibility.Visible;
            try
            {
                var result = await VisualOcrModelManager.Shared.InstallAsync(new Progress<ModelDownloadProgress>(value =>
                {
                    if (_disposed) return;
                    progress.Value = value.TotalBytes == 0 ? 0 : value.DownloadedBytes * 100d / value.TotalBytes;
                    _visualOcrStatus.Text = $"{progress.Value:0}% · {FormatFileSize(value.DownloadedBytes)} / {FormatFileSize(value.TotalBytes)} · {value.CurrentFileName}";
                }), cancellation.Token);
                if (!_disposed)
                {
                    RefreshVisualOcrStatus();
                    _settingsViewModel.SetStatus(result.IsSuccess ? "视觉识别模型已就绪，可在识别方式中选择 Qwen3-VL 4B。" : result.ErrorMessage ?? "下载失败。");
                }
            }
            finally
            {
                _visualOcrDownloadCancellation = null;
                if (!_disposed)
                { _visualOcrDownload.IsEnabled = true; cancel.Visibility = progress.Visibility = Visibility.Collapsed; }
            }
        };
        var open = new Button { Content = "打开目录", Margin = new Thickness(8, 0, 0, 0) };
        open.SetResourceReference(StyleProperty, "SecondaryButton");
        open.Click += (_, _) =>
        {
            try
            {
                Directory.CreateDirectory(VisualOcrModelManager.Shared.InstallationDirectory);
                Process.Start(new ProcessStartInfo(VisualOcrModelManager.Shared.InstallationDirectory) { UseShellExecute = true });
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            { _settingsViewModel.SetStatus("无法打开视觉模型目录。"); }
        };
        buttons.Children.Add(_visualOcrDownload); buttons.Children.Add(cancel); buttons.Children.Add(open);
        panel.Children.Add(buttons);
        RefreshVisualOcrStatus();
        var card = SettingsCard(panel);
        card.Margin = new Thickness(0, 22, 0, 28);
        return card;
    }

    private void RefreshVisualOcrStatus()
    {
        if (_visualOcrStatus is null || _visualOcrDownload is null) return;
        var status = VisualOcrModelManager.Shared.GetStatus();
        _visualOcrStatus.Text = status.IsInstalled ? "已安装 · 可选择 Qwen3-VL 4B 视觉识别" :
            $"未安装 · 需下载约 {FormatFileSize(status.DownloadSize)}";
        _visualOcrDownload.Content = status.IsInstalled ? "校验 / 修复" : "下载视觉模型";
    }
}
