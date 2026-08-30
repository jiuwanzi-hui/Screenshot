using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Channels;
using Screenshot.App.Core;

namespace Screenshot.App.Capture;

/// <summary>
/// Opt-in timing trace for diagnosing shortcut capture hitches. It is disabled
/// unless SNAPCUT_CAPTURE_TIMING is set to a non-empty value.
/// </summary>
internal static class CaptureTimingDiagnostics
{
    // Timing traces are for local Debug investigations only. Release builds
    // keep the call sites as no-ops so shipped packages cannot enable them.
#if DEBUG
    private static readonly bool IsEnabled = IsDiagnosticsEnabled();
#else
    private static bool IsEnabled => false;
#endif
    private static readonly Channel<string> Queue =
        Channel.CreateUnbounded<string>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });
    private static int _started;

    private static bool IsDiagnosticsEnabled()
    {
        if (!string.IsNullOrWhiteSpace(
                Environment.GetEnvironmentVariable("SNAPCUT_CAPTURE_TIMING")))
        {
            return true;
        }

        try
        {
            return File.Exists(Path.Combine(
                AppMetadata.DiagnosticsDirectoryPath,
                "capture-timing.enabled"));
        }
        catch
        {
            return false;
        }
    }

    public static TimingScope Begin(string stage, string? details = null)
    {
        if (!IsEnabled)
        {
            return default;
        }

        EnsureWriter();
        Write($"begin stage={stage}{FormatDetails(details)}");
        return new TimingScope(stage);
    }

    public static void Mark(string stage, string? details = null)
    {
        if (!IsEnabled)
        {
            return;
        }

        EnsureWriter();
        Write($"mark stage={stage}{FormatDetails(details)}");
    }

    private static string FormatDetails(string? details) =>
        string.IsNullOrWhiteSpace(details) ? string.Empty : $" {details}";

    private static void EnsureWriter()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                Directory.CreateDirectory(AppMetadata.DiagnosticsDirectoryPath);
                var path = Path.Combine(
                    AppMetadata.DiagnosticsDirectoryPath,
                    "capture-timing.log");
                await foreach (var line in Queue.Reader.ReadAllAsync())
                {
                    await File.AppendAllTextAsync(path, line + Environment.NewLine)
                        .ConfigureAwait(false);
                }
            }
            catch
            {
                // Diagnostics must never affect capture input or application exit.
            }
        });
    }

    private static void Write(string message)
    {
        var timestamp = DateTimeOffset.Now.ToString(
            "yyyy-MM-dd HH:mm:ss.fffffff zzz",
            CultureInfo.InvariantCulture);
        var line = new StringBuilder()
            .Append(timestamp)
            .Append(" tid=")
            .Append(Environment.CurrentManagedThreadId)
            .Append(' ')
            .Append(message)
            .ToString();
        Queue.Writer.TryWrite(line);
    }

    public readonly struct TimingScope : IDisposable
    {
        private readonly string? _stage;
        private readonly long _startedAt;

        internal TimingScope(string stage)
        {
            _stage = stage;
            _startedAt = Stopwatch.GetTimestamp();
        }

        public void Dispose()
        {
            if (_stage is null || !IsEnabled)
            {
                return;
            }

            var elapsed = Stopwatch.GetElapsedTime(_startedAt);
            Mark($"end:{_stage}", $"elapsedMs={elapsed.TotalMilliseconds:0.###}");
        }
    }
}
