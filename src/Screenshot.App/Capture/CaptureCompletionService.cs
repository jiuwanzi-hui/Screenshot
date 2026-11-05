using System.Windows.Media.Imaging;
using Screenshot.App.Core;

namespace Screenshot.App.Capture;

internal static class CaptureCompletionService
{
    // The checkmark and AI entry commit exactly the same output, including
    // scale, auto-save and history. The returned frozen bitmap survives disposal.
    internal static async Task<BitmapSource?> CompleteAsync(CapturedImage captured, CaptureOverlayOptions options,
        Action closeBeforePicker, Func<BitmapSource, Task>? copy = null,
        Func<PngSaveLocationMode, string, Task<string?>>? resolveDirectory = null)
    {
        using var image = captured.CreateScaled(options.ScreenshotScalePercent);
        string? savedPath = null;
        if (options.AutoSaveOnComplete)
        {
            if (options.PngSaveLocationMode == PngSaveLocationMode.AskEveryTime) closeBeforePicker();
            var directory = await (resolveDirectory ?? PngSaveLocationService.ResolveAsync)(options.PngSaveLocationMode, options.SaveDirectory);
            if (directory is null) return null;
            savedPath = CaptureFileService.SaveAsPng(image, directory);
        }
        var preview = image.Preview;
        await (copy ?? (bitmap => ClipboardImageService.SetImageAsync(bitmap)))(preview);
        if (options.KeepHistory)
        {
            var item = options.HistoryService.Add(image, options.HistoryLimit);
            if (savedPath is not null) item?.MarkSaved(savedPath);
        }
        return preview;
    }
}
