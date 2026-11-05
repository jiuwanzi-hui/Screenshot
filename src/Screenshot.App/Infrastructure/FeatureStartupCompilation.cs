using System.IO;
using System.Runtime;
using Screenshot.App.Core;

namespace Screenshot.App.Infrastructure;

// A small runtime method-order profile, never a cached window, model or image.
// Only the on-demand capture/settings worker uses it. Its one background JIT thread
// overlaps cold UI initialization and goes away with the worker.
internal static class FeatureStartupCompilation
{
    internal const string Argument = "--capture-startup";
    internal const string SettingsArgument = "--settings-startup";
    private static int _active;
    private static System.Threading.Timer? _stopTimer;

    internal static void Start(string[] args)
    {
        if (args.Length < 2 || args[0] != FeatureProcessHost.WorkerArgument ||
            Environment.ProcessorCount < 2) return;
        var profile = args.Contains(SettingsArgument, StringComparer.Ordinal) ? "settings-startup.profile"
            : args.Contains(Argument, StringComparer.Ordinal) ? "capture-startup.profile" : null;
        if (profile is null) return;
        try
        {
            var directory = Path.Combine(AppMetadata.DataDirectoryPath, "StartupCache");
            Directory.CreateDirectory(directory);
            ProfileOptimization.SetProfileRoot(directory);
            ProfileOptimization.StartProfile(profile);
            Volatile.Write(ref _active, 1);
            // A cancelled/failed cold start may never publish a frame. Stop
            // recording promptly in that case too; this is not an idle poll.
            _stopTimer = BackgroundTimer.Create(_ => Complete());
            _stopTimer.Change(TimeSpan.FromSeconds(3), Timeout.InfiniteTimeSpan);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        { /* Read-only portable locations retain the normal JIT path. */ }
    }

    internal static void Complete()
    {
        if (Interlocked.Exchange(ref _active, 0) == 0) return;
        Interlocked.Exchange(ref _stopTimer, null)?.Dispose();
        _ = Task.Run(() => ProfileOptimization.StartProfile(null));
    }
}
