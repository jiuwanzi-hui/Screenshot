using System.Diagnostics;
using System.IO;
using RapidOcrNet;
using Screenshot.App.Text;
using SkiaSharp;
using Xunit.Abstractions;

namespace Screenshot.App.Tests;

// Optional local sample evaluation. No user image or absolute local path is
// embedded in the project, and a recognition result is not an accuracy claim.
public sealed class OcrModelComparisonTests(ITestOutputHelper output)
{
    [LocalSampleFact]
    public void CompareOriginalSampleWithSmallAndMediumUsingApplicationThresholds()
    {
        var modelRoot = Environment.GetEnvironmentVariable("SNAPCUT_OCR_COMPARE_ROOT")!;
        var sample = Environment.GetEnvironmentVariable("SNAPCUT_OCR_COMPARE_IMAGE")!;
        using var bitmap = SKBitmap.Decode(sample);
        Assert.NotNull(bitmap);
        foreach (var size in new[] { "Small", "Medium" })
        {
            var directory = Path.Combine(modelRoot, "PP-OCRv6-" + size);
            var models = (size == "Small" ? RapidOcrModelSet.PPOCRv6Small : RapidOcrModelSet.PPOCRv6Medium) with
            {
                DetModelPath = Path.Combine(directory, $"PP-OCRv6_det_{size.ToLowerInvariant()}.onnx"),
                RecModelPath = Path.Combine(directory, $"PP-OCRv6_rec_{size.ToLowerInvariant()}.onnx"),
                ClsModelPath = Path.Combine(directory, "ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx"),
                KeysPath = Path.Combine(directory, "ppocrv6_dict.txt"),
            };
            var watch = Stopwatch.StartNew();
            using var engine = HighQualityOcrService.CreateEngine(models, Math.Max(1, Math.Min(Environment.ProcessorCount / 2, 8)));
            var initialization = watch.ElapsedMilliseconds;
            var extended = Environment.GetEnvironmentVariable("SNAPCUT_OCR_COMPARE_EXTENDED") == "1";
            foreach (var scale in extended ? new[] { 1, 2, 3, 4, 0 } : new[] { 1, 2 })
            {
                using var enlarged = scale <= 1 ? null : bitmap.Resize(new SKImageInfo(bitmap.Width * scale, bitmap.Height * scale),
                    new SKSamplingOptions(SKFilterMode.Linear));
                var input = enlarged ?? bitmap;
                var options = RapidOcrOptions.PPOCRv6 with { ImgResize = Math.Min(1600, Math.Max(input.Width, input.Height)),
                    LimitSideLen = 0, MaxSideLen = 1600, DoAngle = false, ReturnWordBox = true, TextScore = 0.45f };
                if (scale == 0) options = RapidOcrOptions.PPOCRv6 with { DoAngle = false, ReturnWordBox = true, TextScore = 0.45f };
                watch.Restart();
                var result = engine.Detect(input, options);
                output.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { Model = size, Scale = scale,
                    InitializationMs = initialization, RecognitionMs = watch.ElapsedMilliseconds,
                    Blocks = result.TextBlocks.Select(block => new { block.Text, Bounds = block.BoxPoints.Select(point => new { point.X, point.Y }) }) }));
                // The application's small-image retry runs only for an empty first pass.
                if (!extended && result.TextBlocks.Any(block => !string.IsNullOrWhiteSpace(block.Text))) break;
            }
        }
    }

    private sealed class LocalSampleFactAttribute : FactAttribute
    {
        public LocalSampleFactAttribute()
        {
            if (!File.Exists(Environment.GetEnvironmentVariable("SNAPCUT_OCR_COMPARE_IMAGE")) ||
                !Directory.Exists(Environment.GetEnvironmentVariable("SNAPCUT_OCR_COMPARE_ROOT")))
                Skip = "Set local sample and model root to compare OCR models.";
        }
    }
}
