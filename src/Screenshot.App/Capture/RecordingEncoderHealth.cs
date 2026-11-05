using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Screenshot.App.Core;

namespace Screenshot.App.Capture;

/// <summary>Short-lived, output-specific fallback after an observed encoder failure.</summary>
internal sealed class RecordingEncoderHealth(string path)
{
    private static readonly object FileGate = new();
    internal static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);

    internal static bool IsEncoderFailure(string error) =>
        error.Contains("0xc00d4a3e", StringComparison.OrdinalIgnoreCase) ||
        (error.Contains("Write error", StringComparison.OrdinalIgnoreCase) &&
         error.Contains("video encoder", StringComparison.OrdinalIgnoreCase));

    internal static string CreateKey(string displayIdentity, VideoRecordingCodec codec) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{Environment.OSVersion.VersionString}|{displayIdentity}|{codec}")));

    // Callers construct recorders and report failures on background workers.
    // No polling, WMI queries or synchronous UI/input-hook disk access.
    internal bool HasRecentFailure(string key, string deviceName, VideoRecordingCodec codec)
    {
        lock (FileGate)
        {
            if (Read().TryGetValue(key, out var when) && IsRecent(when)) return true;
            // 4.0.2 already writes this diagnostic. Honor its observed hardware
            // failure on the same output/OS/codec instead of making the customer
            // reproduce it once more after installing the fix.
            var diagnostic = Path.Combine(Path.GetDirectoryName(path)!, "recording-error.log");
            try
            {
                var file = new FileInfo(diagnostic);
                if (!file.Exists || file.Length > 65536 || !IsRecent(file.LastWriteTimeUtc)) return false;
                var lines = File.ReadAllLines(diagnostic);
                return lines.Contains($"OS: {Environment.OSVersion.VersionString}") &&
                    lines.Contains($"Display: {deviceName}") &&
                    lines.Contains("Mode: 硬件加速录制") &&
                    lines.Any(line => line.StartsWith("Capture: ", StringComparison.Ordinal) &&
                        line.Contains($", {codec}, ", StringComparison.Ordinal)) &&
                    IsEncoderFailure(string.Join('\n', lines));
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }
    }

    internal void RememberFailure(string key)
    {
        lock (FileGate)
        {
            string? temporary = null;
            try
            {
                var entries = Read().Where(pair => IsRecent(pair.Value))
                    .OrderByDescending(pair => pair.Value).Take(15).ToDictionary();
                entries[key] = DateTimeOffset.UtcNow;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllText(temporary, JsonSerializer.Serialize(entries));
                File.Move(temporary, path, overwrite: true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            finally
            {
                if (temporary is not null)
                {
                    try { File.Delete(temporary); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
        }
    }

    private Dictionary<string, DateTimeOffset> Read()
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length > 65536) return [];
            return JsonSerializer.Deserialize<Dictionary<string, DateTimeOffset>>(File.ReadAllText(path)) ?? [];
        }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
        catch (JsonException) { return []; }
    }

    private static bool IsRecent(DateTimeOffset when) =>
        DateTimeOffset.UtcNow - when is var age && age >= TimeSpan.Zero && age < Lifetime;
}
