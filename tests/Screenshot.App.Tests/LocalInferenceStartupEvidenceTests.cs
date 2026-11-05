using System.Diagnostics;
using System.IO;
using Screenshot.App.Text;

namespace Screenshot.App.Tests;

public sealed class LocalInferenceStartupEvidenceTests
{
    [Fact]
    public void StartupBufferIsBoundedAndCannotRetainTranslationTextAfterReady()
    {
        var evidence = new LocalInferenceStartupEvidence();
        evidence.Append(new string('x', 40000));
        evidence.Append("Vulkan initialization failed");
        Assert.InRange(evidence.Snapshot().Length, 1, LocalInferenceStartupEvidence.MaximumCharacters);
        Assert.EndsWith("Vulkan initialization failed" + Environment.NewLine, evidence.Snapshot());
        evidence.Stop();
        evidence.Append("private prompt or translation");
        Assert.Empty(evidence.Snapshot());
        evidence.Reset();
        evidence.Append("fresh startup");
        Assert.DoesNotContain("private", evidence.Snapshot());
    }

    [Fact]
    public void CpuFallbackHidesVulkanDevicesOnlyInsideItsChildProcess()
    {
        var previous = Environment.GetEnvironmentVariable("GGML_VK_VISIBLE_DEVICES");
        var start = new ProcessStartInfo();
        start.Environment["GGML_VK_VISIBLE_DEVICES"] = "0,1";
        LocalTranslationInferenceSession.ConfigureCpuEnvironment(start);
        Assert.Equal(string.Empty, start.Environment["GGML_VK_VISIBLE_DEVICES"]);
        Assert.Equal(previous, Environment.GetEnvironmentVariable("GGML_VK_VISIBLE_DEVICES"));
    }

    [Fact]
    public async Task StartupFailureRetainsModelAndCauseAndAwaitsTheErrorReport()
    {
        using var manager = new LocalLargeTranslationModelManager(lightweight: true);
        var reported = false;
        using var session = new LocalTranslationInferenceSession(manager,
            _ => throw new InvalidOperationException("driver failed in probe"),
            async (model, details) =>
            {
                await Task.Yield();
                Assert.Equal("Hy-MT2 1.8B", model);
                Assert.Contains("driver failed in probe", details);
                reported = true;
                return "错误记录：isolated.log";
            });
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            session.RunAsync((_, _) => Task.FromResult(true), CancellationToken.None));
        Assert.True(reported);
        Assert.Contains("Hy-MT2 1.8B", error.Message);
        Assert.Contains("isolated.log", error.Message);
        Assert.DoesNotContain("选择轻量模型", error.Message);
    }

    [Fact]
    public async Task CancelledStartupIsNotRecordedAsAnError()
    {
        using var cancellation = new CancellationTokenSource();
        using var manager = new LocalLargeTranslationModelManager(lightweight: true);
        using var session = new LocalTranslationInferenceSession(manager,
            _ => { cancellation.Cancel(); throw new OperationCanceledException(cancellation.Token); },
            (_, _) => throw new Xunit.Sdk.XunitException("Cancellation must not write an error log."));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            session.RunAsync((_, _) => Task.FromResult(true), cancellation.Token));
    }

    [Fact]
    public async Task FailureLogFallsBackAndAppendsBeforeReturning()
    {
        var root = Path.Combine(Path.GetTempPath(), "SnapCut-startup-log-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var blocked = Path.Combine(root, "blocked");
            File.WriteAllText(blocked, "file prevents directory creation");
            var fallback = Path.Combine(root, "fallback");
            var result = await LocalInferenceStartupEvidence.SaveFailureAsync("Hy-MT2 1.8B", "first failure", blocked, fallback);
            Assert.Contains(fallback, result);
            await LocalInferenceStartupEvidence.SaveFailureAsync("Hy-MT2 1.8B", "second failure", blocked, fallback);
            var log = File.ReadAllText(Path.Combine(fallback, "translation-startup-errors.log"));
            Assert.Contains("first failure", log);
            Assert.Contains("second failure", log);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
