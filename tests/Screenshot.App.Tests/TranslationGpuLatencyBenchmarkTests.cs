using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using Screenshot.App.Core;
using Screenshot.App.Text;

namespace Screenshot.App.Tests;

// Explicit local measurement of CPU support threads with identical GPU/model,
// prompts, context, parallelism and completeness checks. Never changes app settings.
public sealed class TranslationGpuLatencyBenchmarkTests
{
    private static readonly JsonSerializerOptions ReportOptions = new() { WriteIndented = true };

    [ThreadBenchmarkFact]
    public async Task CompareSlotCountsAcrossTwoPages()
    {
        var root = Environment.GetEnvironmentVariable("SNAPCUT_THREAD_BENCH_BUILD")!;
        var inputPath = Environment.GetEnvironmentVariable("SNAPCUT_THREAD_BENCH_INPUT")!;
        var reportPath = Environment.GetEnvironmentVariable("SNAPCUT_THREAD_BENCH_REPORT")!;
        using var inputJson = JsonDocument.Parse(await File.ReadAllTextAsync(inputPath));
        var input = inputJson.RootElement.GetProperty("input").EnumerateArray().Select(x => x.GetString()!).ToArray();
        var reports = new List<object>();
        foreach (var slots in new[] { 1, 2, 4, 2 })
        {
            var threads = 4;
            using var manager = new LocalLargeTranslationModelManager(Path.Combine(root, "TranslationModels", "Hy-MT2-1.8B"), lightweight: true);
            var prepareTimer = Stopwatch.StartNew();
            var executable = await manager.PrepareInferenceRuntimeAsync(false, CancellationToken.None);
            var prepareSeconds = prepareTimer.Elapsed.TotalSeconds;
            var listing = new ProcessStartInfo(executable, "--list-devices")
            { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true, RedirectStandardError = true };
            LocalInferenceRuntime.ConfigureEnvironment(listing, false);
            using var probe = Process.Start(listing)!;
            var probeOut = probe.StandardOutput.ReadToEndAsync();
            var probeErr = probe.StandardError.ReadToEndAsync();
            await probe.WaitForExitAsync();
            var device = LocalTranslationInferenceSession.SelectDevice(await probeOut + await probeErr);
            Assert.NotNull(device);
            Assert.True(device.IsDiscrete && device.FreeMemoryMiB >= 8192, "Benchmark needs enough free discrete VRAM.");
            using var reservation = new TcpListener(IPAddress.Loopback, 0);
            reservation.Start();
            var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
            reservation.Stop();
            var start = new ProcessStartInfo(executable)
            { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = Path.GetDirectoryName(executable)!,
                RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in new[] { "-m", manager.ModelPath, "--host", "127.0.0.1", "--port", port.ToString(CultureInfo.InvariantCulture),
                "-c", (4096 * slots).ToString(CultureInfo.InvariantCulture), "-np", slots.ToString(CultureInfo.InvariantCulture), "-t", threads.ToString(CultureInfo.InvariantCulture), "-tb", threads.ToString(CultureInfo.InvariantCulture),
                "-b", "512", "-ub", "256", "--poll", "0", "--poll-batch", "0", "--no-webui", "--log-verbosity", "4",
                "--cache-ram", "0", "--no-jinja", "--chat-template", "chatml", "--fit", "off", "--split-mode", "none",
                "--no-warmup", "--device", device.Id, "-ngl", "all", "--load-mode", "none" })
                start.ArgumentList.Add(argument);
            LocalInferenceRuntime.ConfigureEnvironment(start, false);
            var timer = Stopwatch.StartNew();
            using var process = Process.Start(start)!;
            // Dispose the session first so its timer cannot access a disposed Process.
            using var session = new LocalTranslationInferenceSession(manager);
            try
            {
                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();
                var endpoint = new Uri($"http://127.0.0.1:{port}/");
                using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false });
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
                while (true)
                {
                    Assert.False(process.HasExited, "Native server exited while loading.");
                    try
                    {
                        using var response = await client.GetAsync(new Uri(endpoint, "health"), deadline.Token);
                        if (response.IsSuccessStatusCode) break;
                    }
                    catch (HttpRequestException) { }
                    await Task.Delay(30, deadline.Token);
                }
                var loadingSeconds = timer.Elapsed.TotalSeconds;
                var type = typeof(LocalTranslationInferenceSession);
                type.GetField("_process", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(session, process);
                type.GetField("_endpoint", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(session, endpoint);
                type.GetProperty("Parallelism", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(session, slots);
                type.GetProperty("RuntimeDescription", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(session,
                    $"显卡 {device.Name}；{threads} 个 CPU 线程，{slots} 路并发（本地对比）");
                typeof(LocalLargeTranslationModelManager).GetField("_inferenceSession", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .SetValue(manager, new Lazy<LocalTranslationInferenceSession>(() => session));
                using var offline = new OfflineTranslationModelManager(Path.Combine(root, "TranslationModels"));
                var provider = new OrderedTranslationProvider([new LocalLargeModelTranslationProvider(manager,
                    new OfflineTranslationProvider(offline))]);
                for (var run = 0; run < 4; run++)
                {
                    process.Refresh();
                    var beforeCpu = process.TotalProcessorTime;
                    timer.Restart();
                    var page = run < 3 ? input : inputJson.RootElement.GetProperty("secondInput").EnumerateArray().Select(x => x.GetString()!).ToArray();
                    var result = await provider.TranslateSegmentsAsync(page, "auto", "zh-Hans", deadline.Token);
                    var seconds = timer.Elapsed.TotalSeconds;
                    process.Refresh();
                    reports.Add(new { slots, threads, run, prepareSeconds, loadingSeconds, seconds,
                        totalSeconds = seconds + (run == 0 ? loadingSeconds : 0),
                        cpuSeconds = (process.TotalProcessorTime - beforeCpu).TotalSeconds,
                        peakWorkingSetMiB = process.PeakWorkingSet64 / 1048576d,
                        result.IsSuccess, result.ErrorMessage, result.Notice, result.FailedSegmentIndices, result.Segments });
                    await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(reports, ReportOptions));
                    Assert.True(result.IsSuccess, result.ErrorMessage);
                    Assert.Equal(page.Length, result.Segments.Count);
                    Assert.Empty(result.FailedSegmentIndices ?? []);
                }
                session.Dispose();
                await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(5));
                await File.WriteAllTextAsync(reportPath + $".{slots}.native.log", await stderr);
            }
            finally
            {
                // A startup failure happens before session ownership is assigned.
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
            }
        }
    }

    private sealed class ThreadBenchmarkFactAttribute : FactAttribute
    {
        public ThreadBenchmarkFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("SNAPCUT_RUN_GPU_LATENCY_BENCHMARK") != "1")
                Skip = "Opt-in local GPU comparison with installed models and a previously recorded page input.";
        }
    }
}
