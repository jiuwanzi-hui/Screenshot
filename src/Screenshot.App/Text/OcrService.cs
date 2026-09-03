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
using Screenshot.App.Core;

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
            using var timing = CaptureTimingDiagnostics.Begin(
                "ocr-windows",
                $"language={languageTag}");
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

            // Resizing a full desktop capture with GDI+ is synchronous and can
            // take a noticeable slice of a low-end CPU. Keep it off the WPF
            // dispatcher; the OCR WinRT awaits below remain asynchronous.
            using var ocrBitmap = await Task.Run(
                () => PrepareBitmap(capturedImage.Bitmap),
                cancellationToken);
            var recognition = await RecognizeBitmapAsync(
                engine,
                ocrBitmap,
                capturedImage.Bitmap.Width,
                capturedImage.Bitmap.Height,
                cancellationToken);

            var englishLanguage = OcrEngine.AvailableRecognizerLanguages
                .FirstOrDefault(candidate => candidate.LanguageTag.StartsWith(
                    "en",
                    StringComparison.OrdinalIgnoreCase));
            var activeEngine = engine;
            if (!languageTag.StartsWith("en", StringComparison.OrdinalIgnoreCase) &&
                englishLanguage is not null &&
                ShouldPreferEnglishLanguage(recognition.Text))
            {
                var englishEngine = OcrEngine.TryCreateFromLanguage(englishLanguage);
                if (englishEngine is not null)
                {
                    var englishRecognition = await RecognizeBitmapAsync(
                        englishEngine,
                        ocrBitmap,
                        capturedImage.Bitmap.Width,
                        capturedImage.Bitmap.Height,
                        cancellationToken);
                    if (englishRecognition.Regions.Count > 0)
                    {
                        recognition = englishRecognition;
                        activeEngine = englishEngine;
                    }
                }
            }

            // Windows OCR can miss small text in a narrow side column when a
            // very wide screenshot is submitted as one bitmap. Re-run only
            // that missing edge at the same scale as the rest of the image,
            // reusing the already-created engine. This keeps normal captures
            // on the fast single-pass path while recovering right-side UI.
            if (ShouldProbeRightEdge(capturedImage.Bitmap, recognition))
            {
                var source = capturedImage.Bitmap;
                var tileLeft = Math.Max(0, (int)Math.Round(source.Width * 0.64));
                var tileRectangle = new Rectangle(
                    tileLeft,
                    0,
                    source.Width - tileLeft,
                    source.Height);
                using var tile = source.Clone(
                    tileRectangle,
                    PixelFormat.Format32bppPArgb);
                using var preparedTile = PrepareBitmap(tile);
                var tileRecognition = await RecognizeBitmapAsync(
                    activeEngine,
                    preparedTile,
                    tile.Width,
                    tile.Height,
                    cancellationToken);
                recognition = MergeEdgeRecognition(
                    recognition,
                    tileRecognition,
                    tileRectangle.X,
                    tileRectangle.Y);
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

    private static async Task<OcrRecognitionResult> RecognizeBitmapAsync(
        OcrEngine engine,
        Bitmap bitmap,
        int sourceWidth,
        int sourceHeight,
        CancellationToken cancellationToken)
    {
        using var bitmapStream = new MemoryStream();
        bitmap.Save(bitmapStream, ImageFormat.Png);
        var stream = bitmapStream.ToArray()
            .AsBuffer()
            .AsStream()
            .AsRandomAccessStream();
        stream.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(stream)
            .AsTask(cancellationToken);
        using var softwareBitmap = await decoder
            .GetSoftwareBitmapAsync()
            .AsTask(cancellationToken);
        var result = await engine
            .RecognizeAsync(softwareBitmap)
            .AsTask(cancellationToken);
        return BuildRecognitionResult(
            result,
            (double)bitmap.Width / Math.Max(1, sourceWidth),
            (double)bitmap.Height / Math.Max(1, sourceHeight));
    }

    private static bool ShouldProbeRightEdge(
        Bitmap source,
        OcrRecognitionResult recognition)
    {
        if (source.Width < 1100 || source.Height < 120 ||
            !recognition.IsSuccess)
        {
            return false;
        }

        var edgeStart = source.Width * 0.68;
        var edgeRegions = recognition.Regions.Count(region =>
            region.X + region.Width >= edgeStart);
        return edgeRegions < 2;
    }

    private static OcrRecognitionResult MergeEdgeRecognition(
        OcrRecognitionResult primary,
        OcrRecognitionResult edge,
        int offsetX,
        int offsetY)
    {
        if (!edge.IsSuccess || edge.Regions.Count == 0)
        {
            return primary;
        }

        var regions = primary.Regions.ToList();
        foreach (var candidate in edge.Regions)
        {
            var shifted = candidate with
            {
                X = candidate.X + offsetX,
                Y = candidate.Y + offsetY,
            };
            if (regions.Any(existing =>
                    string.Equals(existing.Text, shifted.Text,
                        StringComparison.OrdinalIgnoreCase) &&
                    OverlapRatio(existing, shifted) >= 0.55))
            {
                continue;
            }

            regions.Add(shifted);
        }

        var words = primary.Words.ToList();
        foreach (var candidate in edge.Words)
        {
            var shifted = candidate with
            {
                X = candidate.X + offsetX,
                Y = candidate.Y + offsetY,
            };
            if (!words.Any(existing =>
                    string.Equals(existing.Text, shifted.Text,
                        StringComparison.OrdinalIgnoreCase) &&
                    OverlapRatio(existing, shifted) >= 0.55))
            {
                words.Add(shifted);
            }
        }

        var ordered = regions
            .OrderBy(region => region.Y)
            .ThenBy(region => region.X)
            .ToArray();
        return primary with
        {
            Text = string.Join(Environment.NewLine, ordered.Select(region => region.Text)),
            Regions = ordered,
            Words = words,
        };
    }

    private static double OverlapRatio(
        OcrTextRegion left,
        OcrTextRegion right)
    {
        var overlapWidth = Math.Max(0, Math.Min(
            left.X + left.Width,
            right.X + right.Width) - Math.Max(left.X, right.X));
        var overlapHeight = Math.Max(0, Math.Min(
            left.Y + left.Height,
            right.Y + right.Height) - Math.Max(left.Y, right.Y));
        var overlap = overlapWidth * overlapHeight;
        var smaller = Math.Min(
            left.Width * left.Height,
            right.Width * right.Height);
        return smaller <= 0 ? 0 : overlap / smaller;
    }

    private static double OverlapRatio(
        OcrWordRegion left,
        OcrWordRegion right)
    {
        var overlapWidth = Math.Max(0, Math.Min(
            left.X + left.Width,
            right.X + right.Width) - Math.Max(left.X, right.X));
        var overlapHeight = Math.Max(0, Math.Min(
            left.Y + left.Height,
            right.Y + right.Height) - Math.Max(left.Y, right.Y));
        var overlap = overlapWidth * overlapHeight;
        var smaller = Math.Min(
            left.Width * left.Height,
            right.Width * right.Height);
        return smaller <= 0 ? 0 : overlap / smaller;
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
