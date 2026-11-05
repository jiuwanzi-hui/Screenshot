using System.IO;
using System.Text.Json;
using Screenshot.App.Core;

namespace Screenshot.App.Text;

internal sealed record TranslationHardwareProfile(string Fingerprint, ComputeResources Cpu,
    LocalTranslationInferenceSession.InferenceDevice? Device, string? ProbeFailure, DateTimeOffset? RetryAfter);

internal sealed class LocalTranslationHardwareCache(string path)
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    internal async Task<TranslationHardwareProfile?> ReadAsync(string fingerprint, CancellationToken token)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 32 * 1024) return null;
            var profile = JsonSerializer.Deserialize<TranslationHardwareProfile>(await File.ReadAllTextAsync(path, token).ConfigureAwait(false));
            return IsCurrent(profile, fingerprint, DateTimeOffset.UtcNow) ? profile : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    internal static bool IsCurrent(TranslationHardwareProfile? profile, string fingerprint, DateTimeOffset now) =>
        profile is not null && profile.Fingerprint == fingerprint &&
        (profile.RetryAfter is null || now < profile.RetryAfter) && profile.Cpu.LogicalCores > 0 &&
        (profile.Device is null || profile.Device.Id is { } id && id.StartsWith("Vulkan", StringComparison.Ordinal) &&
            int.TryParse(id.AsSpan(6), out var index) && index >= 0 && !string.IsNullOrWhiteSpace(profile.Device.Name));

    internal async Task SaveAsync(TranslationHardwareProfile profile, CancellationToken token)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".pending";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // Dynamic RAM/load/VRAM numbers are deliberately not persisted.
            profile = profile with { Cpu = profile.Cpu with { AvailableRamBytes = 0, CpuBusyFraction = -1 },
                Device = profile.Device is { } device ? device with { FreeMemoryMiB = 0 } : null };
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(profile, Options), token).ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { /* Optional cache, not model failure. */ }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }
}
