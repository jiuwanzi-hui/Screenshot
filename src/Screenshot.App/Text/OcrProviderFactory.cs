using Screenshot.App.Capture;
using Screenshot.App.Core;

namespace Screenshot.App.Text;

public static class OcrProviderFactory
{
    public static async Task<OcrRecognitionResult> RecognizeAsync(
        CapturedImage image,
        AppSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        using var timing = CaptureTimingDiagnostics.Begin(
            "ocr-request",
            $"engine={settings.OcrEngine}");
        return await (settings.OcrEngine == OcrEngineMode.PaddleOcrV6
            ? HighQualityOcrService.RecognizeAsync(
                image,
                cancellationToken: cancellationToken)
            : OcrService.RecognizeAsync(
                image,
                settings.OcrLanguageTag,
                cancellationToken));
    }
}
