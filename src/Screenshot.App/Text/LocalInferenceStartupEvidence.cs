using System.IO;
using System.Text;
using Screenshot.App.Core;

namespace Screenshot.App.Text;

// Capture only startup diagnostics: stop before accepting any translation text.
internal sealed class LocalInferenceStartupEvidence
{
    private readonly object _sync = new();
    private readonly StringBuilder _text = new();
    private bool _accepting = true;
    internal const int MaximumCharacters = 16384;

    internal void Append(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        lock (_sync)
        {
            if (!_accepting) return;
            _text.AppendLine(line.Length > MaximumCharacters ? line[^MaximumCharacters..] : line);
            if (_text.Length > MaximumCharacters) _text.Remove(0, _text.Length - MaximumCharacters);
        }
    }

    internal string Snapshot() { lock (_sync) return _text.ToString(); }
    internal void Stop() { lock (_sync) { _accepting = false; _text.Clear(); } }
    internal void Reset() { lock (_sync) { _accepting = true; _text.Clear(); } }

    private static readonly SemaphoreSlim FileGate = new(1, 1);
    internal static Task<string> SaveFailureAsync(string model, string details,
        string? directoryOverride = null, string? fallbackOverride = null) => Task.Run(async () =>
    {
        // Preserve the last native error even when several startup attempts
        // have filled their individual buffers.
        if (details.Length > 32768) details = "[Earlier startup output omitted]\n" + details[^32768..];
        var entry = $"\n--- {DateTimeOffset.Now:O} | SnapCut {AppMetadata.DisplayVersion} | {model} ---\n" +
            $"OS: {Environment.OSVersion}\n{details}\n";
        await FileGate.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (var directory in new[] { directoryOverride ?? AppMetadata.DiagnosticsDirectoryPath,
                fallbackOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SnapCut", "Diagnostics") })
            {
                try
                {
                    Directory.CreateDirectory(directory);
                    var file = Path.Combine(directory, "translation-startup-errors.log");
                    if (File.Exists(file) && new FileInfo(file).Length + Encoding.UTF8.GetByteCount(entry) > 512 * 1024)
                        File.Move(file, file + ".previous", overwrite: true);
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    await File.AppendAllTextAsync(file, entry, Encoding.UTF8, timeout.Token).ConfigureAwait(false);
                    return "错误记录：" + file;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                    System.Security.SecurityException or OperationCanceledException) { }
            }
            return "启动错误记录未能保存。";
        }
        finally { FileGate.Release(); }
    });
}
