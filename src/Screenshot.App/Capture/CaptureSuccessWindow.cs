using System.IO;
using System.Windows.Threading;
using Screenshot.App.Core;
using Screenshot.App.Infrastructure;

namespace Screenshot.App.Capture;

// Draw into a small native HWND independently of capture's WPF composition teardown.
internal static class CaptureSuccessWindow
{
    internal static async Task ShowAsync(ScreenRegion region, string message = "截图成功")
    {
        if (region.IsEmpty) return;
        var application = System.Windows.Application.Current;
        var dispatcher = application?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted) return;
        using var activity = FeatureActivity.Begin();
        try
        {
            await dispatcher.InvokeAsync(async () =>
            {
                using var notice = new ResidentStartupFeedback(message,
                    (application as App)?.FeedbackTheme ?? AppTheme.AuroraMist, region);
                await notice.Closed;
            }, DispatcherPriority.Normal).Task.Unwrap();
        }
        catch (Exception exception)
        {
            // Feedback failure must not undo the copy/save, but must be diagnosable.
            await Task.Run(() =>
            {
                try
                {
                    Directory.CreateDirectory(AppMetadata.DiagnosticsDirectoryPath);
                    App.AppendCrashLog(Path.Combine(AppMetadata.DiagnosticsDirectoryPath, "completion-feedback-errors.log"),
                        $"[{DateTime.Now:O}] {exception}{Environment.NewLine}");
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            });
        }
    }
}
