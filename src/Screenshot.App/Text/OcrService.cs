using System.IO;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;
using Screenshot.App.Capture;

namespace Screenshot.App.Text;

public static class OcrService
{
    public static IReadOnlyList<string> GetAvailableLanguageTags()
    {
        return OcrEngine.AvailableRecognizerLanguages
            .Select(language => language.LanguageTag)
            .OrderBy(languageTag => languageTag, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static async Task<OcrRecognitionResult> RecognizeAsync(
        CapturedImage capturedImage,
        string languageTag,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(capturedImage);

        if (string.IsNullOrWhiteSpace(languageTag))
        {
            return OcrRecognitionResult.Failure("请在设置中指定识别语言。");
        }

        try
        {
            var language = new Language(languageTag);

            if (!OcrEngine.IsLanguageSupported(language))
            {
                return OcrRecognitionResult.Failure(
                    $"Windows 未安装 {languageTag} 的 OCR 语言包。");
            }

            var engine = OcrEngine.TryCreateFromLanguage(language);

            if (engine is null)
            {
                return OcrRecognitionResult.Failure("无法创建 Windows OCR 引擎。");
            }

            using var ocrBitmap = PrepareBitmap(capturedImage.Bitmap);
            using var bitmapStream = new MemoryStream();
            ocrBitmap.Save(bitmapStream, ImageFormat.Png);
            var stream = bitmapStream.ToArray().AsBuffer().AsStream().AsRandomAccessStream();

            stream.Seek(0);
            var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(cancellationToken);
            using var softwareBitmap = await decoder.GetSoftwareBitmapAsync().AsTask(cancellationToken);
            var result = await engine.RecognizeAsync(softwareBitmap).AsTask(cancellationToken);
            var scaleX = (double)ocrBitmap.Width / capturedImage.Bitmap.Width;
            var scaleY = (double)ocrBitmap.Height / capturedImage.Bitmap.Height;
            var recognition = BuildRecognitionResult(result, scaleX, scaleY);

            var englishLanguage = OcrEngine.AvailableRecognizerLanguages
                .FirstOrDefault(candidate => candidate.LanguageTag.StartsWith(
                    "en",
                    StringComparison.OrdinalIgnoreCase));
            if (!languageTag.StartsWith("en", StringComparison.OrdinalIgnoreCase) &&
                englishLanguage is not null &&
                ShouldPreferEnglishLanguage(recognition.Text))
            {
                var englishEngine = OcrEngine.TryCreateFromLanguage(englishLanguage);
                if (englishEngine is not null)
                {
                    var englishResult = await englishEngine
                        .RecognizeAsync(softwareBitmap)
                        .AsTask(cancellationToken);
                    var englishRecognition = BuildRecognitionResult(
                        englishResult,
                        scaleX,
                        scaleY);
                    if (englishRecognition.Regions.Count > 0)
                    {
                        recognition = englishRecognition;
                    }
                }
            }

            return recognition;
        }
        catch (OperationCanceledException)
        {
            return OcrRecognitionResult.Failure("文字识别已取消。");
        }
        catch (Exception)
        {
            return OcrRecognitionResult.Failure("文字识别失败，请检查语言包后重试。");
        }
    }

    internal static bool ShouldPreferEnglishLanguage(string text)
    {
        var letterCount = 0;
        var latinLetterCount = 0;
        foreach (var character in text)
        {
            if (!char.IsLetter(character))
            {
                continue;
            }

            letterCount++;
            if (character is >= 'A' and <= 'Z' or >= 'a' and <= 'z')
            {
                latinLetterCount++;
            }
        }

        return latinLetterCount >= 8 &&
               latinLetterCount >= letterCount * 0.7;
    }

    private static OcrRecognitionResult BuildRecognitionResult(
        OcrResult result,
        double scaleX,
        double scaleY)
    {
            var text = string.Join(
                Environment.NewLine,
                result.Lines.Select(line => line.Text));
            var regions = new List<OcrTextRegion>();
            var words = new List<OcrWordRegion>();

            foreach (var line in result.Lines)
            {
                if (line.Words.Count == 0 || string.IsNullOrWhiteSpace(line.Text))
                {
                    continue;
                }

                var left = line.Words.Min(word => word.BoundingRect.X);
                var top = line.Words.Min(word => word.BoundingRect.Y);
                var right = line.Words.Max(
                    word => word.BoundingRect.X + word.BoundingRect.Width);
                var bottom = line.Words.Max(
                    word => word.BoundingRect.Y + word.BoundingRect.Height);
                regions.Add(new OcrTextRegion(
                    line.Text,
                    left / scaleX,
                    top / scaleY,
                    (right - left) / scaleX,
                    (bottom - top) / scaleY)
                {
                    EstimatedFontSize = Math.Clamp(
                        ((bottom - top) / scaleY) / 1.12,
                        8,
                        64),
                });

                words.AddRange(line.Words
                    .Where(word => !string.IsNullOrWhiteSpace(word.Text))
                    .Select(word => new OcrWordRegion(
                        word.Text,
                        word.BoundingRect.X / scaleX,
                        word.BoundingRect.Y / scaleY,
                        word.BoundingRect.Width / scaleX,
                        word.BoundingRect.Height / scaleY)));
            }

        return new OcrRecognitionResult(true, text, ErrorMessage: null)
        {
            Regions = regions,
            Words = words,
        };
    }

    private static Bitmap PrepareBitmap(Bitmap source)
    {
        var scale = Math.Min(
            2d,
            Math.Min(3200d / source.Width, 3200d / source.Height));
        scale = Math.Max(1d, scale);

        if (Math.Abs(scale - 1d) < 0.01d)
        {
            return (Bitmap)source.Clone();
        }

        var prepared = new Bitmap(
            (int)Math.Round(source.Width * scale),
            (int)Math.Round(source.Height * scale),
            PixelFormat.Format32bppPArgb);
        using var graphics = Graphics.FromImage(prepared);
        graphics.CompositingMode = CompositingMode.SourceCopy;
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.SmoothingMode = SmoothingMode.HighQuality;
        graphics.DrawImage(
            source,
            new Rectangle(0, 0, prepared.Width, prepared.Height));
        return prepared;
    }
}
