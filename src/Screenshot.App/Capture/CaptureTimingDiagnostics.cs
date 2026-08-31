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
    // The diagnostic symbol is used only for a temporary investigation build.
    // Normal Release builds keep all input-trace call sites compiled out.
#if DEBUG || SNAPCUT_CAPTURE_DIAGNOSTICS
    private static readonly bool IsEnabled = IsDiagnosticsEnabled();
#else
    private static bool IsEnabled => false;
#endif
    private static readonly Channel<string> Queue =
        Channel.CreateBounded<string>(new BoundedChannelOptions(20000)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest,
        });
    private static int _started;
    private static long _inputWindowUntil;
    private static long _lastMouseMoveTimestamp;
    private static int _mouseMoveSampleCount;

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

    [Conditional("SNAPCUT_CAPTURE_DIAGNOSTICS")]
    public static void Input(string message)
    {
        if (!IsEnabled)
        {
            return;
        }

        EnsureWriter();
        Write($"input {message}");
    }

    public static bool Enabled => IsEnabled;

    [Conditional("SNAPCUT_CAPTURE_DIAGNOSTICS")]
    public static void BeginInputWindow(string reason)
    {
        if (!IsEnabled)
        {
            return;
        }

        Interlocked.Exchange(
            ref _inputWindowUntil,
            Stopwatch.GetTimestamp() + Stopwatch.Frequency * 3);
        Interlocked.Exchange(ref _lastMouseMoveTimestamp, 0);
        Interlocked.Exchange(ref _mouseMoveSampleCount, 0);
        Input($"input-window reason={reason}");
    }

    [Conditional("SNAPCUT_CAPTURE_DIAGNOSTICS")]
    public static void MouseMove(
        string source,
        int x,
        int y,
        int deltaX = 0,
        int deltaY = 0,
        string? state = null)
    {
        if (!IsEnabled)
        {
            return;
        }

        var now = Stopwatch.GetTimestamp();
        var until = Interlocked.Read(ref _inputWindowUntil);
        if (now > until)
        {
            return;
        }

        var previous = Interlocked.Exchange(ref _lastMouseMoveTimestamp, now);
        var gapMs = previous == 0
            ? 0
            : Stopwatch.GetElapsedTime(previous).TotalMilliseconds;
        var sample = Interlocked.Increment(ref _mouseMoveSampleCount);
        if (sample <= 40 || gapMs >= 8)
        {
            Input(
                $"mouse-move source={source} x={x} y={y} dx={deltaX} dy={deltaY} " +
                $"gapMs={gapMs:0.###} sample={sample} state={state ?? "none"}");
        }
    }

    public static void Exception(string stage, Exception exception)
    {
        if (!IsEnabled)
        {
            return;
        }

        EnsureWriter();
        Write($"exception stage={stage} type={exception.GetType().Name} " +
            $"message={exception.Message.Replace(Environment.NewLine, " ")}");
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
