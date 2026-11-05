using System.Diagnostics;
using System.IO;
using System.Reflection;
using Screenshot.App.Core;
using Screenshot.App.Text;

namespace Screenshot.App.Tests;

public sealed class TranslationPreparationTests
{
    [Theory]
    [InlineData(TranslationProviderKind.Offline, OfflineTranslationEngine.HunyuanLightweight, true)]
    [InlineData(TranslationProviderKind.Offline, OfflineTranslationEngine.QwenLargeModel, true)]
    [InlineData(TranslationProviderKind.Offline, OfflineTranslationEngine.Mozilla, false)]
    [InlineData(TranslationProviderKind.Online, OfflineTranslationEngine.HunyuanLightweight, false)]
    [InlineData(TranslationProviderKind.Public, OfflineTranslationEngine.HunyuanLightweight, false)]
    public void PreparationNeverPassesEarlierProviders(TranslationProviderKind first, OfflineTranslationEngine engine, bool expected) =>
        Assert.Equal(expected, TranslationPreparation.IsEligible(new AppSettings { SettingsVersion = 3,
            TranslationProviderPriority = [first], OfflineTranslationEngine = engine }));

    [Fact]
    public async Task MissingHardwareProfileDoesNotProbeOrStartModel()
    {
        using var manager = new LocalLargeTranslationModelManager(Path.GetTempPath(), lightweight: true);
        using var session = new LocalTranslationInferenceSession(manager,
            _ => throw new InvalidOperationException("Preparation must not probe."));
        await session.PrepareDuringRecognitionAsync(Guid.NewGuid(), CancellationToken.None);
        Assert.Null(Worker(session));
    }

    [LocalRuntimeTheory]
    [InlineData(true)]
    public async Task PreparedGpuIsReusedAndOnlyItsOwnerCanCancelUnusedWork(bool lightweight)
    {
        using var manager = new LocalLargeTranslationModelManager(
            Environment.GetEnvironmentVariable("SNAPCUT_TRANSLATION_MODEL_DIRECTORY"), lightweight: lightweight);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        using var session = new LocalTranslationInferenceSession(manager);
        // Establish a real validated hardware profile, then stop the first worker.
        await session.RunAsync((_, _) => Task.FromResult(true), deadline.Token);
        typeof(LocalTranslationInferenceSession).GetMethod("StopWorker", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(session, null);
        var owner = Guid.NewGuid();
        await session.PrepareDuringRecognitionAsync(owner, deadline.Token);
        Assert.Contains("同时准备", session.RuntimeDescription);
        using var prepared = Process.GetProcessById(Assert.IsType<Process>(Worker(session)).Id);
        await session.CancelUnusedPreparationAsync(Guid.NewGuid());
        Assert.False(prepared.HasExited);
        var text = await session.RunAsync((_, token) => session.CompleteAsync(
            LocalLargeModelTranslationProvider.CreateLightweightPrompt("Please save the document.", "zh"), 128, token), deadline.Token);
        Assert.Contains("保存", text);
        Assert.Equal(prepared.Id, Assert.IsType<Process>(Worker(session)).Id);
        await session.CancelUnusedPreparationAsync(owner);
        Assert.False(prepared.HasExited); // Claimed by real translation; retain short reuse.
        await prepared.WaitForExitAsync(deadline.Token); // Native process actually exits at idle.
        owner = Guid.NewGuid();
        await session.PrepareDuringRecognitionAsync(owner, deadline.Token);
        using var unused = Process.GetProcessById(Assert.IsType<Process>(Worker(session)).Id);
        await session.CancelUnusedPreparationAsync(owner);
        await unused.WaitForExitAsync(deadline.Token);
        Assert.Null(Worker(session));
    }

    private static Process? Worker(LocalTranslationInferenceSession session) =>
        (Process?)typeof(LocalTranslationInferenceSession).GetField("_process", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session);
}
