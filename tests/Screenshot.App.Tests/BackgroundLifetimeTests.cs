using System.Reflection;
using System.Runtime.CompilerServices;
using Screenshot.App.Core;
using Screenshot.App.Text;

namespace Screenshot.App.Tests;

public sealed class BackgroundLifetimeTests
{
    [Fact]
    public async Task ServiceTimerDoesNotKeepOrReplayTheCreatingOperationsContext()
    {
        var local = new AsyncLocal<object?>();
        var fired = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var (timer, retired) = CreateTimer(local, fired);
        using (timer)
        {
            Collect();
            Assert.False(retired.IsAlive);
            timer.Change(0, Timeout.Infinite);
            Assert.Null(await fired.Task.WaitAsync(TimeSpan.FromSeconds(3)));
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (System.Threading.Timer Timer, WeakReference Retired) CreateTimer(
        AsyncLocal<object?> local, TaskCompletionSource<object?> fired)
    {
        var owner = new byte[1024 * 1024];
        var retired = new WeakReference(owner);
        local.Value = owner;
        var timer = BackgroundTimer.Create(_ => fired.TrySetResult(local.Value));
        local.Value = null;
        return (timer, retired);
    }

    [Fact]
    public void CompletedProgressDropsTheWindowEvenIfAQueuedContextOutlivesIt()
    {
        var (context, retired) = CaptureDisposedProgress();
        Collect();
        Assert.False(retired.IsAlive);
        ExecutionContext.Run(context, _ => TranslationProgress.Report("late update"), null);
        GC.KeepAlive(context);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (ExecutionContext Context, WeakReference Retired) CaptureDisposedProgress()
    {
        var owner = new List<string>();
        var retired = new WeakReference(owner);
        using var scope = TranslationProgress.Begin(owner.Add);
        return (ExecutionContext.Capture()!, retired);
    }

    [Fact]
    public void ModelSessionCreatedDuringTranslationDoesNotRetainItsProgressOwner()
    {
        using var manager = new LocalLargeTranslationModelManager("unused-model-directory", lightweight: true);
        var retired = CreateSessionDuringProgress(manager);
        Collect();
        Assert.False(retired.IsAlive);
        GC.KeepAlive(manager.InferenceSession);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CreateSessionDuringProgress(LocalLargeTranslationModelManager manager)
    {
        var owner = new List<string>();
        using var progress = TranslationProgress.Begin(owner.Add);
        _ = manager.InferenceSession;
        return new WeakReference(owner);
    }

    [Fact]
    public void NativeLanguageDetectorReleasesIdleStateAndRecreatesTheSameResult()
    {
        const string text = "Please save the document before closing the application window.";
        var original = Cld3OfflineLanguageDetector.Shared.Detect(text);
        Assert.True(original.IsSuccess, original.ErrorMessage);
        var type = typeof(Cld3OfflineLanguageDetector);
        const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
        var detector = type.GetField("_detector", flags)!;
        var release = type.GetMethod("ReleaseIdleDetector", flags)!;
        var originalDetector = detector.GetValue(null);
        Assert.NotNull(originalDetector);
        release.Invoke(null, null);
        Assert.Same(originalDetector, detector.GetValue(null));
        type.GetField("_lastUse", flags)!.SetValue(null, Environment.TickCount64 - 15_000);
        release.Invoke(null, null);
        Assert.Null(detector.GetValue(null));
        Assert.Equal(original, Cld3OfflineLanguageDetector.Shared.Detect(text));
    }

    [Fact]
    public void DisposingAnOldProgressTwiceDoesNotClearANewerOperation()
    {
        var old = TranslationProgress.Begin(_ => { });
        old.Dispose();
        var messages = new List<string>();
        using var current = TranslationProgress.Begin(messages.Add);
        old.Dispose();
        TranslationProgress.Report("current");
        Assert.Equal(["current"], messages);
    }

    private static void Collect()
    {
        // Test-only reachability check, never a product working-set trim.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }
}
