using System.Drawing;
using System.Drawing.Imaging;
using Screenshot.App.Capture;

namespace Screenshot.App.Text;

internal static class TallImageOcrService
{
    // Overlap gives the detector context around each ownership band. Recognize
    // one tile at a time; the selected OCR engine owns the native thread budget.
    private const int BandHeight = 1216;
    private const int ContextRows = 192;

    internal static async Task<OcrRecognitionResult> RecognizeAsync(CapturedImage image,
        Func<CapturedImage, CancellationToken, Task<OcrRecognitionResult>> recognize,
        CancellationToken cancellationToken)
    {
        var bitmap = image.Bitmap;
        if (bitmap.Height <= 2200 || bitmap.Height <= bitmap.Width * 1.6)
            return await recognize(image, cancellationToken).ConfigureAwait(false);

        var regions = new List<OcrTextRegion>();
        var words = new List<OcrWordRegion>();
        for (var top = 0; top < bitmap.Height; top += BandHeight)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bottom = Math.Min(bitmap.Height, top + BandHeight);
            var sampleTop = Math.Max(0, top - ContextRows);
            var sampleBottom = Math.Min(bitmap.Height, bottom + ContextRows);
            using var tile = await Task.Run(() => new CapturedImage(bitmap.Clone(
                new Rectangle(0, sampleTop, bitmap.Width, sampleBottom - sampleTop),
                PixelFormat.Format32bppArgb)), cancellationToken).ConfigureAwait(false);
            var result = await recognize(tile, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!result.IsSuccess) return OcrRecognitionResult.Failure(
                $"长图第 {top / BandHeight + 1} 部分识别失败：{result.ErrorMessage}");
            if (result.Regions.Count == 0 && !string.IsNullOrWhiteSpace(result.Text))
                return OcrRecognitionResult.Failure("识别引擎未返回文字位置，无法可靠合并长图，请更换识别引擎。");
            bool Owns(double y, double height) => y + sampleTop + height / 2 >= top && y + sampleTop + height / 2 < bottom;
            regions.AddRange(result.Regions.Where(region => Owns(region.Y, region.Height))
                .Select(region => region with { Y = region.Y + sampleTop }));
            words.AddRange(result.Words.Where(word => Owns(word.Y, word.Height))
                .Select(word => word with { Y = word.Y + sampleTop }));
        }
        var ordered = regions.OrderBy(region => region.Y).ThenBy(region => region.X).ToArray();
        return new(true, string.Join(Environment.NewLine, ordered.Select(region => region.Text)), null)
            { Regions = ordered, Words = words };
    }
}
