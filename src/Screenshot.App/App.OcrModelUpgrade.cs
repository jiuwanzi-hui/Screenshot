using System.IO;
using System.Windows;
using System.Windows.Threading;
using Screenshot.App.Core;
using Screenshot.App.Infrastructure;
using Screenshot.App.Presentation;
using Screenshot.App.Text;

namespace Screenshot.App;

public partial class App
{
    private OcrModelUpgradeResult? _pendingOcrModelUpgrade;

    private async Task CheckOcrModelUpgradeAsync()
    {
        try
        {
            var result = await Task.Run(() =>
            {
                return OcrModelUpgrade.Prepare(AppMetadata.TranslationModelsDirectoryPath,
                    AppMetadata.DataDirectoryPath);
            });
            if (_isShuttingDown || !result.NeedsReminder) return;
            _pendingOcrModelUpgrade = result;
            TryShowOcrModelUpgrade();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Keep startup usable; the next startup retries the recorded migration.
            _pendingStartupStatus = "识别模型升级检查未完成：" + exception.Message;
        }
    }

    private void TryShowOcrModelUpgrade()
    {
        if (_pendingOcrModelUpgrade is not { } upgrade || _isCaptureInProgress || _isShuttingDown) return;
        _pendingOcrModelUpgrade = null;
        if (_featureHost is not null)
        {
            _ = SendFeatureAsync(new("command") { Command = "ocr-model-upgrade", Text = upgrade.CleanupError });
            return;
        }
        ShowMainWindow();
        _mainWindow?.RestoreSection(2);
        _ = Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
        {
            if (_isShuttingDown || _mainWindow is not { IsVisible: true } window) return;
            var message = "日常识别已恢复使用更快的 PP-OCRv6 Small。\n\n" +
                "请在“内容识别”中点击“下载识别模型”，补充约 29.7 MiB 的 Small 文件。已有 Medium 保留用于困难小图和竖排艺术字增强，普通页面不会加载它。\n\n" +
                "两个模型共用现有方向模型和字符表；仍按需加载，空闲约 10 秒退出识别工作进程。";
            window.ShowStatus("请补充 Small 快速识别模型；已有 Medium 保留用于艺术字增强。");
            LocalizedMessageBox.Show(window, message, "恢复快速识别", MessageBoxButton.OK, MessageBoxImage.Information);
            _ = MarkOcrModelUpgradeNotifiedAsync();
        }));
    }

    private static async Task MarkOcrModelUpgradeNotifiedAsync()
    {
        try { await Task.Run(() => OcrModelUpgrade.MarkNotified(AppMetadata.DataDirectoryPath)); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }
}
