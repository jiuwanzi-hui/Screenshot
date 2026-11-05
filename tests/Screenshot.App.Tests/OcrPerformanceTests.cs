using System.Diagnostics;
using System.IO;
using System.Text.Json;
using RapidOcrNet;
using Screenshot.App.Text;
using SkiaSharp;

namespace Screenshot.App.Tests;

public sealed class OcrPerformanceTests
{
    [OcrPerformanceFact]
    public void ComparesTallCropOrientationAcrossModelSizes()
    {
        using var manager = new HighQualityOcrModelManager(Environment.GetEnvironmentVariable("SNAPCUT_OCR_MODEL_DIRECTORY"));
        var report = new List<object>();
        foreach (var medium in new[] { false, true })
        {
            var models = (medium ? RapidOcrModelSet.PPOCRv6Medium : RapidOcrModelSet.PPOCRv6Small) with {
                DetModelPath = medium ? manager.ArtisticDetectionModelPath : manager.DetectionModelPath,
                RecModelPath = medium ? manager.ArtisticRecognitionModelPath : manager.RecognitionModelPath,
                ClsModelPath = manager.ClassificationModelPath, KeysPath = manager.DictionaryPath };
            using var engine = HighQualityOcrService.CreateEngine(models, 12);
            foreach (var path in Environment.GetEnvironmentVariable("SNAPCUT_OCR_IMAGES")!.Split('|'))
            foreach (var counterClockwise in new[] { false, true })
            {
                using var bitmap = SKBitmap.Decode(path);
                var timer = Stopwatch.StartNew();
                var result = engine.Detect(bitmap, RapidOcrOptions.PPOCRv6 with {
                    ImgResize = Math.Min(1600, Math.Max(bitmap.Width, bitmap.Height)), LimitSideLen = 0,
                    MaxSideLen = 1600, DoAngle = false, ReturnWordBox = true, TextScore = .45f,
                    RotateTallCropsCounterClockwise = counterClockwise });
                report.Add(new { image = Path.GetFileName(path), medium, counterClockwise, seconds = timer.Elapsed.TotalSeconds,
                    blocks = result.TextBlocks.Select(b => new { b.Text, score = b.CharScores is { Length: > 0 } scores ? scores.Average() : 0, b.BoxPoints }).ToArray() });
                File.WriteAllText(Environment.GetEnvironmentVariable("SNAPCUT_OCR_REPORT")!, JsonSerializer.Serialize(report,
                    new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            }
        }
    }

    [OcrPerformanceFact]
    public void ComparesGraphOptimizationsWithIdenticalModelsAndPixels()
    {
        using var manager = new HighQualityOcrModelManager(Environment.GetEnvironmentVariable("SNAPCUT_OCR_MODEL_DIRECTORY"));
        var models = RapidOcrModelSet.PPOCRv6Small with { DetModelPath = manager.DetectionModelPath,
            ClsModelPath = manager.ClassificationModelPath, RecModelPath = manager.RecognitionModelPath, KeysPath = manager.DictionaryPath };
        var reports = new List<object>();
        var baseline = new Dictionary<string, string>();
        foreach (var level in new[] { Microsoft.ML.OnnxRuntime.GraphOptimizationLevel.ORT_ENABLE_EXTENDED,
                     Microsoft.ML.OnnxRuntime.GraphOptimizationLevel.ORT_ENABLE_ALL })
        {
            using var options = RapidOcr.GetDefaultSessionOptions(3);
            options.GraphOptimizationLevel = level;
            options.InterOpNumThreads = 1;
            options.AddSessionConfigEntry("session.intra_op.allow_spinning", "0");
            options.AddSessionConfigEntry("session.inter_op.allow_spinning", "0");
            var timer = Stopwatch.StartNew();
            using var engine = new RapidOcr();
            engine.InitModels(models, options);
            var loadSeconds = timer.Elapsed.TotalSeconds;
            foreach (var path in Environment.GetEnvironmentVariable("SNAPCUT_OCR_IMAGES")!.Split('|'))
            {
                using var image = SKBitmap.Decode(path);
                for (var run = 0; run < 3; run++)
                {
                    timer.Restart();
                    var result = engine.Detect(image, RapidOcrOptions.PPOCRv6 with {
                        ImgResize = Math.Min(1600, Math.Max(image.Width, image.Height)), LimitSideLen = 0,
                        MaxSideLen = 1600, DoAngle = false, ReturnWordBox = true, TextScore = .45f });
                    var seconds = timer.Elapsed.TotalSeconds;
                    var text = string.Join("\n", result.TextBlocks.Select(b => b.Text));
                    baseline.TryAdd(path, text);
                    reports.Add(new { image = Path.GetFileName(path), level = level.ToString(), run, loadSeconds, seconds,
                        result.DbNetTime, blocks = result.TextBlocks.Length, sameText = baseline[path] == text, text });
                    File.WriteAllText(Environment.GetEnvironmentVariable("SNAPCUT_OCR_REPORT")!, JsonSerializer.Serialize(reports));
                    Assert.False(string.IsNullOrWhiteSpace(text));
                    Assert.Equal(baseline[path], text);
                }
            }
        }
    }

    [OcrPerformanceFact]
    public async Task MeasuresRealServiceForPagesAndASmallSelection()
    {
        using var manager = new HighQualityOcrModelManager(Environment.GetEnvironmentVariable("SNAPCUT_OCR_MODEL_DIRECTORY"));
        var reports = new List<object>();
        foreach (var path in Environment.GetEnvironmentVariable("SNAPCUT_OCR_IMAGES")!.Split('|'))
        {
            using var image = new Screenshot.App.Capture.CapturedImage(new System.Drawing.Bitmap(path));
            string? previous = null;
            for (var run = 0; run < 2; run++)
            {
                var watch = Stopwatch.StartNew();
                var result = await HighQualityOcrService.RecognizeAsync(image, manager);
                reports.Add(new { image = Path.GetFileName(path), threads = Screenshot.App.Core.HeavyWorkloadBudget.OcrThreadCount,
                    run, seconds = watch.Elapsed.TotalSeconds, regions = result.Regions.Count, result.IsSuccess,
                    sameText = previous is null || result.Text == previous, result.Text, result.Performance });
                Assert.True(result.IsSuccess, result.ErrorMessage);
                previous = result.Text;
            }
        }
        using var small = new Screenshot.App.Capture.CapturedImage(new System.Drawing.Bitmap(600, 100));
        using (var graphics = System.Drawing.Graphics.FromImage(small.Bitmap))
        using (var font = new System.Drawing.Font("Microsoft YaHei UI", 18))
        {
            graphics.Clear(System.Drawing.Color.White);
            graphics.DrawString("Screenshot translation: 25 users\n请保存文档，再选择目标语言。", font, System.Drawing.Brushes.Black, 8, 8);
        }
        var timer = Stopwatch.StartNew();
        var recognized = await HighQualityOcrService.RecognizeAsync(small, manager);
        reports.Add(new { image = "600x100 small selection", threads = Screenshot.App.Core.HeavyWorkloadBudget.OcrThreadCount,
            seconds = timer.Elapsed.TotalSeconds, recognized.IsSuccess });
        Assert.True(recognized.IsSuccess, recognized.ErrorMessage);
        Assert.Contains("25", recognized.Text);
        File.WriteAllText(Environment.GetEnvironmentVariable("SNAPCUT_OCR_REPORT")!, JsonSerializer.Serialize(reports));
    }

    [OcrPerformanceFact]
    public void MeasuresNativeThreadBudgetsWithoutChangingModelsOrResolution()
    {
        using var manager = new HighQualityOcrModelManager(Environment.GetEnvironmentVariable("SNAPCUT_OCR_MODEL_DIRECTORY"));
        var models = RapidOcrModelSet.PPOCRv6Small with { DetModelPath = manager.DetectionModelPath,
            ClsModelPath = manager.ClassificationModelPath, RecModelPath = manager.RecognitionModelPath, KeysPath = manager.DictionaryPath };
        var reports = new List<object>();
        var expected = new Dictionary<string, string>();
        using var process = Process.GetCurrentProcess();
        var originalAffinity = process.ProcessorAffinity;
        if (long.TryParse(Environment.GetEnvironmentVariable("SNAPCUT_OCR_AFFINITY"), out var affinity))
            process.ProcessorAffinity = (nint)affinity;
        try
        {
        var budgets = Environment.GetEnvironmentVariable("SNAPCUT_OCR_THREADS")?.Split(',').Select(int.Parse).ToArray()
            ?? [2, 4, 6, 8, 12, 16];
        foreach (var threads in budgets)
        {
            var watch = Stopwatch.StartNew();
            using var engine = HighQualityOcrService.CreateEngine(models, threads);
            if (int.TryParse(Environment.GetEnvironmentVariable("SNAPCUT_OCR_PARALLELISM"), out var parallelism))
                engine.RecognitionParallelism = parallelism;
            var initializationSeconds = watch.Elapsed.TotalSeconds;
            foreach (var path in Environment.GetEnvironmentVariable("SNAPCUT_OCR_IMAGES")!.Split('|'))
            {
                using var bitmap = SKBitmap.Decode(path);
                var options = RapidOcrOptions.PPOCRv6 with { ImgResize = Math.Min(1600, Math.Max(bitmap.Width, bitmap.Height)),
                    LimitSideLen = 0, MaxSideLen = 1600, DoAngle = false, ReturnWordBox = true, TextScore = 0.45f };
                for (var run = 0; run < 2; run++)
                {
                    watch.Restart();
                    var result = engine.Detect(bitmap, options);
                    var seconds = watch.Elapsed.TotalSeconds;
                    var key = Path.GetFileName(path);
                    var text = string.Join("\n", result.TextBlocks.Select(block => block.Text));
                    expected.TryAdd(key, text);
                    reports.Add(new { image = key, threads, engine.RecognitionParallelism, initializationSeconds, run, seconds,
                        result.DbNetTime, result.DetectTime, recognitionMs = result.TextBlocks.Sum(block => block.CrnnTime),
                        blocks = result.TextBlocks.Length, sameText = expected[key] == text });
                    File.WriteAllText(Environment.GetEnvironmentVariable("SNAPCUT_OCR_REPORT")!, JsonSerializer.Serialize(reports));
                }
            }
        }
        }
        finally { process.ProcessorAffinity = originalAffinity; }
    }
}

public sealed class OcrPerformanceFactAttribute : FactAttribute
{
    public OcrPerformanceFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("SNAPCUT_RUN_OCR_PERFORMANCE") != "1")
            Skip = "Opt-in native OCR benchmark on supplied files; never captures the desktop.";
    }
}
