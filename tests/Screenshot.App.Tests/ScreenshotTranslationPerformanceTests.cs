using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using Screenshot.App.Capture;
using Screenshot.App.Core;
using Screenshot.App.Infrastructure;
using Screenshot.App.Text;

namespace Screenshot.App.Tests;

public sealed class ScreenshotTranslationPerformanceTests
{
    private static readonly JsonSerializerOptions ReportOptions = new() { WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    [ScreenshotTranslationPerformanceFact]
    public async Task MeasuresSuppliedImageUsingConfiguredModels()
    {
        var directory = Environment.GetEnvironmentVariable("SNAPCUT_PERFORMANCE_BUILD")!;
        var reportPath = Environment.GetEnvironmentVariable("SNAPCUT_PERFORMANCE_REPORT")!;
        var settings = new SettingsStore(Path.Combine(directory, "ScreenshotData", "settings.json")).Load().Settings;
        using var sourceImage = new System.Drawing.Bitmap(Environment.GetEnvironmentVariable("SNAPCUT_PERFORMANCE_IMAGE")!);
        var crop = Environment.GetEnvironmentVariable("SNAPCUT_PERFORMANCE_CROP")?.Split(',').Select(int.Parse).ToArray();
        using var image = new CapturedImage(crop is { Length: 4 }
            ? sourceImage.Clone(new System.Drawing.Rectangle(crop[0], crop[1], crop[2], crop[3]), sourceImage.PixelFormat)
            : new System.Drawing.Bitmap(sourceImage));
        using var ocr = new HighQualityOcrModelManager(Path.Combine(directory, "TranslationModels", Screenshot.App.Core.AppMetadata.HighQualityOcrModelDirectoryName));
        var watch = Stopwatch.StartNew();
        var recognition = await HighQualityOcrService.RecognizeAsync(image, ocr);
        Assert.True(recognition.IsSuccess, recognition.ErrorMessage);
        var ocrSeconds = watch.Elapsed.TotalSeconds;
        watch.Restart();
        var regions = TranslationPresentationLayout.GroupParagraphs(TranslationInkBounds.Refine(image.Bitmap,
            TranslationPresentationLayout.TightenToWordBounds(recognition.Regions, recognition.Words)));
        var layoutSeconds = watch.Elapsed.TotalSeconds;
        var input = regions.Select(region => region.Text).ToArray();
        var reports = new List<object>();
        var resources = await Task.Run(ComputeResourceBudget.Capture);
        var credentials = new DpapiTranslationCredentialStore(Path.Combine(directory, "ScreenshotData", "translation-credentials.dat"));
        var lightweight = Environment.GetEnvironmentVariable("SNAPCUT_PERFORMANCE_LIGHTWEIGHT") == "1";
        using var manager = new LocalLargeTranslationModelManager(Path.Combine(directory, "TranslationModels",
            lightweight ? "Hy-MT2-1.8B" : AppMetadata.LocalLargeTranslationModelDirectoryName), lightweight: lightweight,
            bundledPackagesDirectory: Path.Combine(directory, "OfflinePackages"));
        if (Environment.GetEnvironmentVariable("SNAPCUT_PERFORMANCE_CPU") == "1")
            typeof(LocalLargeTranslationModelManager).GetField("_inferenceSession",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(manager,
                new Lazy<LocalTranslationInferenceSession>(() => new(manager,
                    _ => Task.FromResult<LocalTranslationInferenceSession.InferenceDevice?>(null))));
        // Explicit, test-only comparison of GPU slot counts; never overrides user settings.
        if (int.TryParse(Environment.GetEnvironmentVariable("SNAPCUT_PERFORMANCE_GPU_SLOTS"), out var slots))
        {
            var listing = new ProcessStartInfo(manager.ExecutablePath!, "--list-devices")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using var probe = Process.Start(listing)!;
            var stdout = probe.StandardOutput.ReadToEndAsync();
            var stderr = probe.StandardError.ReadToEndAsync();
            await probe.WaitForExitAsync();
            var device = LocalTranslationInferenceSession.SelectDevice(await stdout + await stderr);
            Assert.NotNull(device);
            if (long.TryParse(Environment.GetEnvironmentVariable("SNAPCUT_PERFORMANCE_GPU_BUDGET_MIB"), out var budget))
                device = device with { FreeMemoryMiB = budget };
            typeof(LocalLargeTranslationModelManager).GetField("_inferenceSession",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(manager,
                new Lazy<LocalTranslationInferenceSession>(() => new(manager, _ => Task.FromResult<LocalTranslationInferenceSession.InferenceDevice?>(device with { Parallelism = slots }))));
        }
        using var offline = new OfflineTranslationModelManager(Path.Combine(directory, "TranslationModels"));
        using var handler = new RequestCounter();
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(2) };
        var mode = Environment.GetEnvironmentVariable("SNAPCUT_PERFORMANCE_MODE") ?? "both";
        if (mode != "online")
        {
            var provider = new OrderedTranslationProvider([new LocalLargeModelTranslationProvider(manager,
                new OfflineTranslationProvider(offline, settings.OfflineTranslationQuality))]);
            await Measure("offline-cold", manager.DisplayName, provider);
            await Measure("offline-warm", manager.DisplayName, provider);
        }
        if (mode != "offline")
        {
            var profile = settings.TranslationProfiles.First(profile => profile.IsEnabled);
            var provider = new OrderedTranslationProvider([new OpenAiCompatibleTranslationProvider(profile.Endpoint,
                profile.Model, credentials.GetApiKey(profile.Id, profile.Provider), client)]);
            await Measure("online", profile.Model, provider);
        }

        async Task Measure(string route, string model, ITranslationProvider provider)
        {
            var before = handler.Count;
            var stages = new System.Collections.Concurrent.ConcurrentQueue<object>();
            watch.Restart();
            using var progress = TranslationProgress.Begin(message => stages.Enqueue(new { seconds = watch.Elapsed.TotalSeconds, message }));
            var result = await provider.TranslateSegmentsAsync(input, "auto", "zh-Hans");
            var seconds = watch.Elapsed.TotalSeconds;
            reports.Add(new { route, model, seconds, totalWithOcrSeconds = ocrSeconds + layoutSeconds + seconds,
                requests = handler.Count - before,
                result.IsSuccess, result.ErrorMessage, result.Notice, result.FailedSegmentIndices, result.Segments, stages });
            await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(new { ocrSeconds, layoutSeconds,
                segments = input.Length, input, reports, resourcesAfterOcr = resources }, ReportOptions));
            // Report partial failures as evidence rather than hiding them behind a timing assertion.
            Assert.True(result.IsSuccess, result.ErrorMessage);
            Assert.Equal(input.Length, result.Segments.Count);
            Assert.Empty(result.FailedSegmentIndices ?? []);
        }
    }

    private sealed class RequestCounter() : DelegatingHandler(new HttpClientHandler())
    {
        internal int Count;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Count);
            return base.SendAsync(request, cancellationToken);
        }
    }
}

public sealed class ScreenshotTranslationPerformanceFactAttribute : FactAttribute
{
    public ScreenshotTranslationPerformanceFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("SNAPCUT_RUN_TRANSLATION_PERFORMANCE") != "1")
            Skip = "Explicit opt-in required: uses installed models and the configured online service quota; no desktop capture.";
    }
}
