using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using Screenshot.App.Core;

namespace Screenshot.App.Capture;

internal sealed class ScrollCaptureDiagnostics
{
    private const int MaximumLogFiles = 5;
#if DEBUG
    private const bool IsDetailedLoggingEnabled = true;
#else
    private const bool IsDetailedLoggingEnabled = false;
#endif
    private readonly ConcurrentQueue<DiagnosticEvent> _events = new();
    private readonly long _startedTimestamp = Stopwatch.GetTimestamp();
    private readonly string _sessionId = DateTime.Now.ToString(
        "yyyyMMdd-HHmmss-fff",
        CultureInfo.InvariantCulture);
    private int _flushed;

    public string SessionId => _sessionId;

    public void Record(string name, params (string Name, object? Value)[] values)
    {
        if (!ShouldRecord(name))
        {
            return;
        }

        var fields = new Dictionary<string, object?>(
            values.Length,
            StringComparer.Ordinal);
        foreach (var value in values)
        {
            fields[value.Name] = value.Value;
        }

        _events.Enqueue(new DiagnosticEvent(
            ElapsedMilliseconds: Stopwatch.GetElapsedTime(
                _startedTimestamp).TotalMilliseconds,
            Name: name,
            Fields: fields));
    }

    public void FlushInBackground()
    {
        if (Interlocked.Exchange(ref _flushed, 1) != 0)
        {
            return;
        }

        var events = _events.ToArray();
        _ = Task.Run(() => Write(events));
    }

    private void Write(IReadOnlyList<DiagnosticEvent> events)
    {
        if (events.Count == 0)
        {
            return;
        }

        try
        {
            var directory = AppMetadata.DiagnosticsDirectoryPath;
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"scroll-{_sessionId}.jsonl");
            using (var writer = new StreamWriter(path, append: false))
            {
                foreach (var diagnosticEvent in events)
                {
                    writer.WriteLine(JsonSerializer.Serialize(diagnosticEvent));
                }
            }

            foreach (var oldFile in new DirectoryInfo(directory)
                         .GetFiles("scroll-*.jsonl")
                         .OrderByDescending(file => file.CreationTimeUtc)
                         .Skip(MaximumLogFiles))
            {
                try
                {
                    oldFile.Delete();
                }
                catch (Exception)
                {
                    // Rotation is best effort and must not affect capture.
                }
            }
        }
        catch (Exception)
        {
            // Diagnostics must never affect the screenshot workflow.
        }
    }

    internal static bool ShouldRecord(string name) =>
        IsDetailedLoggingEnabled || IsError(name);

    private static bool IsError(string name) =>
        name.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("exception", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("error", StringComparison.OrdinalIgnoreCase);

    private sealed record DiagnosticEvent(
        double ElapsedMilliseconds,
        string Name,
        IReadOnlyDictionary<string, object?> Fields);
}
