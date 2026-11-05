using System.IO;
using System.Text;
using Screenshot.App.Core;

namespace Screenshot.App.Capture;

internal sealed record RecordingErrorLogResult(string? FilePath, string? Error)
{
    internal string Description => FilePath is not null
        ? $"错误记录：{FilePath}"
        : $"错误记录未能保存：{Error}";
}

// Error-only diagnostics in release builds. Ordered background writes finish
// before the recording UI closes; no input/frame trace or idle polling.
internal sealed class RecordingErrorLog(string? directory = null, string? fallbackDirectory = null)
{
    internal const string FileName = "recording-errors.log";
    internal const int MaximumFileBytes = 512 * 1024;
    private static readonly SemaphoreSlim FileGate = new(1, 1);
    private readonly object _queueGate = new();
    private readonly string _session = Guid.NewGuid().ToString("N");
    private Task<RecordingErrorLogResult> _pending = Task.FromResult(new RecordingErrorLogResult(null, null));

    internal Task<RecordingErrorLogResult> WriteAsync(string stage, string context, string error)
    {
        var entry = $"\n--- {DateTimeOffset.Now:O} | session={_session} | pid={Environment.ProcessId} ---\n" +
            $"SnapCut {AppMetadata.DisplayVersion}\nOS: {Environment.OSVersion.VersionString}\n" +
            $"Stage: {stage}\n{context}\n{error}\n";
        if (entry.Length > 32768) entry = entry[..32768] + "\n[truncated]\n";
        lock (_queueGate)
        {
            var previous = _pending;
            return _pending = Task.Run(async () =>
            {
                await previous.ConfigureAwait(false);
                return await WriteCoreAsync(entry).ConfigureAwait(false);
            });
        }
    }

    internal Task<RecordingErrorLogResult> FlushAsync() { lock (_queueGate) return _pending; }

    private async Task<RecordingErrorLogResult> WriteCoreAsync(string entry)
    {
        var paths = new[]
        {
            directory ?? AppMetadata.DiagnosticsDirectoryPath,
            fallbackDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SnapCut", "Diagnostics"),
        };
        var failures = new List<string>();
        await FileGate.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    Directory.CreateDirectory(path);
                    var file = Path.Combine(path, FileName);
                    var text = failures.Count == 0 ? entry : entry + $"Primary log unavailable: {failures[0]}\n";
                    if (File.Exists(file) && new FileInfo(file).Length + Encoding.UTF8.GetByteCount(text) > MaximumFileBytes)
                        File.Move(file, file + ".previous", overwrite: true);
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    await File.AppendAllTextAsync(file, text, Encoding.UTF8, timeout.Token).ConfigureAwait(false);
                    return new(file, null);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                    System.Security.SecurityException or ArgumentException or NotSupportedException or OperationCanceledException)
                {
                    failures.Add($"{path}: {exception.Message}");
                }
            }
            return new(null, string.Join("；", failures));
        }
        finally { FileGate.Release(); }
    }

    internal static string Describe(ScreenRegion region, VideoRecordingCodec codec, int fps, bool systemAudio, bool microphone) =>
        $"Capture: {region.Width}x{region.Height}, {codec}, {RegionVideoRecorder.NormalizeFrameRate(fps)} fps\n" +
        $"Audio: system={systemAudio}, microphone={microphone}";
}
