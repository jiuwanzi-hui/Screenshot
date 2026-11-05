using Screenshot.App.Capture;
using Screenshot.App.Core;

namespace Screenshot.App.Text;

public static class OcrProviderFactory
{
    public static Task<OcrRecognitionResult> RecognizeAsync(CapturedImage image, AppSettings settings,
        CancellationToken cancellationToken = default) => RecognizeAsync(image, settings, null, cancellationToken);

    public static async Task<OcrRecognitionResult> RecognizeAsync(
        CapturedImage image,
        AppSettings settings,
        IProgress<string>? progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        using var timing = CaptureTimingDiagnostics.Begin(
            "ocr-request",
            $"engine={settings.OcrEngine}");
        return await (settings.OcrEngine switch
        {
            OcrEngineMode.PaddleOcrV6 => HighQualityOcrService.RecognizeAsync(image, cancellationToken: cancellationToken),
            OcrEngineMode.QwenVisualOcr => VisualOcrService.Shared.RecognizeAsync(image, progress, cancellationToken),
            _ => OcrService.RecognizeAsync(image, settings.OcrLanguageTag, cancellationToken),
        });
    }
}
