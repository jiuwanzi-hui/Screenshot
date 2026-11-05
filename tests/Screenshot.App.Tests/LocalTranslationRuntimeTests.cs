using System.Diagnostics;
using System.IO;
using System.Reflection;
using Screenshot.App.Text;
using Xunit.Abstractions;

namespace Screenshot.App.Tests;

public sealed class LocalTranslationRuntimeTests(ITestOutputHelper output)
{
    [LocalRuntimeTheory]
    [InlineData(true)]
    public async Task HungDeviceProbeFallsBackToIndependentCpuWorker(bool lightweight)
    {
        using var manager = new LocalLargeTranslationModelManager(
            Environment.GetEnvironmentVariable("SNAPCUT_TRANSLATION_MODEL_DIRECTORY"), lightweight: lightweight);
        var root = Path.Combine(Path.GetTempPath(), "SnapCut-hung-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var pidFile = Path.Combine(root, "pid.txt");
        LocalTranslationInferenceSession? session = null;
        try
        {
            var script = $"[IO.File]::WriteAllText('{pidFile.Replace("'", "''")}', [string]$PID); [Console]::Error.WriteLine('probe entered; simulated driver stall'); Start-Sleep -Seconds 30";
            var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell", "v1.0", "powershell.exe"))
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-NonInteractive");
            start.ArgumentList.Add("-EncodedCommand"); start.ArgumentList.Add(Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script)));
            session = new(manager, token => session!.ProbeDeviceAsync(start, token),
                (model, details) => LocalInferenceStartupEvidence.SaveFailureAsync(model, details, root, root));
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var watch = Stopwatch.StartNew();
            var result = await session.RunAsync((_, token) => session.CompleteAsync(
                LocalLargeModelTranslationProvider.CreateLightweightPrompt("Please save the document.", "zh"), 128, token), deadline.Token);
            output.WriteLine($"Hung probe + actual CPU translation={watch.Elapsed.TotalSeconds:F2}s; {session.RuntimeDescription}; text={result}");
            Assert.Contains("保存", result);
            Assert.Contains("3 秒", session.RuntimeDescription);
            Assert.Contains("使用 CPU", session.RuntimeDescription);
            var evidence = await File.ReadAllTextAsync(Path.Combine(root, "translation-startup-errors.log"));
            Assert.Contains("simulated driver stall", evidence);
            Assert.Contains("CPU 运行库恢复", evidence);
            var id = int.Parse(await File.ReadAllTextAsync(pidFile));
            try
            {
                using var process = Process.GetProcessById(id);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (ArgumentException) { /* Already reaped. */ }
        }
        finally { session?.Dispose(); Directory.Delete(root, recursive: true); }
    }

    [LocalRuntimeTheory]
    [InlineData(true)]
    public async Task NewSessionReusesPersistedHardwareWithoutStartingDeviceProbe(bool lightweight)
    {
        var root = Path.Combine(Path.GetTempPath(), "SnapCut-native-cache-" + Guid.NewGuid().ToString("N"));
        var cache = new LocalTranslationHardwareCache(Path.Combine(root, "hardware.json"));
        try
        {
            for (var run = 0; run < 2; run++)
            {
                using var manager = new LocalLargeTranslationModelManager(
                    Environment.GetEnvironmentVariable("SNAPCUT_TRANSLATION_MODEL_DIRECTORY"), lightweight: lightweight);
                using var session = new LocalTranslationInferenceSession(manager);
                typeof(LocalTranslationInferenceSession).GetField("_hardwareCache", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(session, cache);
                if (run == 1)
                    typeof(LocalTranslationInferenceSession).GetField("_deviceProbe", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(session,
                        new Func<CancellationToken, Task<LocalTranslationInferenceSession.InferenceDevice?>>(
                            _ => throw new Xunit.Sdk.XunitException("A valid persisted profile must not probe Vulkan again.")));
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var watch = Stopwatch.StartNew();
                var result = await session.RunAsync((_, token) => session.CompleteAsync(
                    LocalLargeModelTranslationProvider.CreateLightweightPrompt("Please save the document.", "zh"), 128, token), deadline.Token);
                output.WriteLine($"fresh session {run + 1}: {watch.Elapsed.TotalSeconds:F2}s; {session.RuntimeDescription}; text={result}");
                Assert.Contains("保存", result);
                Assert.Contains("显卡", session.RuntimeDescription);
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [LocalRuntimeTheory]
    [InlineData(true)]
    public async Task FailedNativeStartupPreservesItsFinalErrorAndReleasesWorker(bool lightweight)
    {
        using var installed = new LocalLargeTranslationModelManager(
            Environment.GetEnvironmentVariable("SNAPCUT_TRANSLATION_MODEL_DIRECTORY"), lightweight: lightweight);
        var root = Path.Combine(Path.GetTempPath(), "SnapCut-native-startup-" + Guid.NewGuid().ToString("N"));
        var runtime = Path.Combine(root, "runtime");
        Directory.CreateDirectory(runtime);
        try
        {
            foreach (var source in Directory.EnumerateFiles(Path.GetDirectoryName(installed.ExecutablePath!)!))
                File.Copy(source, Path.Combine(runtime, Path.GetFileName(source)));
            using var manager = new LocalLargeTranslationModelManager(root, lightweight: lightweight,
                bundledPackagesDirectory: installed.InstallationDirectory);
            // An isolated invalid model exercises the real native loader without
            // changing any installed model or launching a translation request.
            await File.WriteAllTextAsync(manager.ModelPath, "invalid model fixture");
            using var session = new LocalTranslationInferenceSession(manager,
                _ => Task.FromResult<LocalTranslationInferenceSession.InferenceDevice?>(null),
                (model, details) => LocalInferenceStartupEvidence.SaveFailureAsync(model, details, root, root));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => session.RunAsync<bool>(
                (_, _) => throw new Xunit.Sdk.XunitException("An invalid model must not reach inference."), timeout.Token));
            var log = await File.ReadAllTextAsync(Path.Combine(root, "translation-startup-errors.log"));
            output.WriteLine(log);
            Assert.Contains("退出码", error.Message);
            Assert.Contains("failed to load model", log, StringComparison.OrdinalIgnoreCase);
            Assert.Null(typeof(LocalTranslationInferenceSession).GetField("_process",
                BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(session));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [LocalRuntimeTheory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public async Task SelectedModelLoadsAndTranslatesOnGpuAndCpu(bool cpu, bool lightweight)
    {
        using var manager = new LocalLargeTranslationModelManager(
            Environment.GetEnvironmentVariable(lightweight ? "SNAPCUT_TRANSLATION_MODEL_DIRECTORY" :
                "SNAPCUT_TRANSLATEGEMMA_MODEL_DIRECTORY"), lightweight: lightweight);
        string Prompt(string text, string source, string target) => lightweight
            ? LocalLargeModelTranslationProvider.CreateLightweightPrompt(text, target)
            : LocalLargeModelTranslationProvider.CreatePrompt(text, source, target);
        using var session = new LocalTranslationInferenceSession(manager,
            cpu ? _ => Task.FromResult<LocalTranslationInferenceSession.InferenceDevice?>(null) : null);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var watch = Stopwatch.StartNew();
        var loaded = 0d;
        var result = await session.RunAsync(async (_, token) =>
        {
            loaded = watch.Elapsed.TotalMilliseconds;
            return await session.CompleteAsync(Prompt(
                "Please save the document before closing the window.", "en", "zh"), 128, token);
        }, deadline.Token);
        output.WriteLine($"Mode={session.RuntimeDescription}; load={loaded:F0} ms; total={watch.Elapsed.TotalMilliseconds:F0} ms; text={result}");
        Assert.Contains("保存", result);
        Assert.Contains("窗口", result);
        Assert.Equal(cpu, session.RuntimeDescription.Contains("使用 CPU", StringComparison.Ordinal));
        if (cpu)
        {
            var process = (Process)typeof(LocalTranslationInferenceSession).GetField("_process",
                BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(session)!;
            var modules = process.Modules.Cast<ProcessModule>().Select(module => module.ModuleName).ToArray();
            Assert.DoesNotContain(modules, name => name.Contains("vulkan", StringComparison.OrdinalIgnoreCase));
            Assert.Contains("cpu", process.MainModule!.FileName, StringComparison.OrdinalIgnoreCase);
            output.WriteLine("CPU isolation: no Vulkan loader/backend module in the running worker.");
        }
        watch.Restart();
        var warm = await session.RunAsync((_, token) => session.CompleteAsync(
            Prompt("打开设置，选择目标语言。", "zh", "en"), 128, token), deadline.Token);
        output.WriteLine($"warm={watch.Elapsed.TotalMilliseconds:F0} ms; text={warm}");
        Assert.Contains("language", warm, StringComparison.OrdinalIgnoreCase);

        // Cancelling an operation must terminate its worker, not leave GPU/CPU
        // work in the background after the screenshot is dismissed.
        using var cancelledWorker = Process.GetProcessById(((Process)typeof(LocalTranslationInferenceSession)
            .GetField("_process", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(session)!).Id);
        using var cancelled = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.RunAsync<bool>(async (_, token) =>
        {
            cancelled.Cancel();
            await Task.Delay(Timeout.Infinite, token);
            return true;
        }, cancelled.Token));
        await cancelledWorker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
        var restarted = await session.RunAsync((_, token) => session.CompleteAsync(
            Prompt("Please save the document.", "en", "zh"), 128, token), deadline.Token);
        Assert.Contains("保存", restarted);
        {
            var owned = (Process)typeof(LocalTranslationInferenceSession)
                .GetField("_process", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(session)!;
            using var worker = Process.GetProcessById(owned.Id);
            output.WriteLine($"worker private commit={worker.PrivateMemorySize64 / 1048576d:F1} MiB; working set={worker.WorkingSet64 / 1048576d:F1} MiB (not VRAM)");
            using var idleDeadline = new CancellationTokenSource(LocalTranslationInferenceSession.IdleTimeout + TimeSpan.FromSeconds(5));
            watch.Restart();
            await worker.WaitForExitAsync(idleDeadline.Token);
            output.WriteLine($"idle release={watch.Elapsed.TotalSeconds:F1} s; worker exited={worker.HasExited}");
            Assert.True(worker.HasExited);
            // Idle release must retain the ability to load again, and a failed
            // operation must release its real native worker as cancellation does.
            var reloaded = await session.RunAsync((_, token) => session.CompleteAsync(
                Prompt("Please save the document.", "en", "zh"), 128, token), deadline.Token);
            Assert.Contains("保存", reloaded);
            using var failedWorker = Process.GetProcessById(((Process)typeof(LocalTranslationInferenceSession)
                .GetField("_process", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(session)!).Id);
            Assert.NotEqual(worker.Id, failedWorker.Id);
            await Assert.ThrowsAsync<InvalidOperationException>(() => session.RunAsync<bool>(
                (_, _) => throw new InvalidOperationException("test failure"), deadline.Token));
            await failedWorker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
        }
    }
}

internal sealed class LocalRuntimeTheoryAttribute : TheoryAttribute
{
    public LocalRuntimeTheoryAttribute()
    {
        if (Environment.GetEnvironmentVariable("SNAPCUT_RUN_TRANSLATION_RUNTIME_TESTS") != "1")
            Skip = "Explicit opt-in required: loads the installed offline model and exercises CPU/GPU.";
    }
}
