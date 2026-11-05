using System.IO;

namespace Screenshot.App.Capture;

internal static class CaptureStartupFailure
{
    internal static void Report(Exception exception)
    {
        // Error-only, bounded, background diagnostics. No images or input
        // tracing, and no file checks on successful hotkey/frame paths.
        _ = Task.Run(async () =>
        {
            try
            {
                var directory = Path.Combine(AppContext.BaseDirectory, "ScreenshotData", "Diagnostics");
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, "capture-startup-error.log");
                var entry = $"{DateTimeOffset.Now:O} Windows={Environment.OSVersion.Version} " +
                    $"App={typeof(CaptureStartupFailure).Assembly.GetName().Version}{Environment.NewLine}{exception}{Environment.NewLine}";
                if (File.Exists(path) && new FileInfo(path).Length > 64 * 1024)
                    await File.WriteAllTextAsync(path, entry).ConfigureAwait(false);
                else await File.AppendAllTextAsync(path, entry).ConfigureAwait(false);
            }
            catch (Exception) { /* Reporting cannot keep a failed overlay alive. */ }
        });
    }
}
