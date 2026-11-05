using System.Globalization;
using System.IO;
using System.Text.Json;

namespace Screenshot.App.Capture;

// Read once by diagnostic builds. Ordinary Release never reads a marker or
// environment variable on the capture/input path.
internal sealed record CaptureTimingSession(string Directory, DateTimeOffset ExpiresUtc)
{
    internal bool IsActive(DateTimeOffset now) => now < ExpiresUtc;

    internal static CaptureTimingSession? Parse(string? marker, string diagnosticsDirectory, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(marker) || marker.Trim().Equals("enabled", StringComparison.OrdinalIgnoreCase))
            return new(diagnosticsDirectory, now.AddMinutes(15));
        try
        {
            if (marker.Length > 4096) return null;
            using var document = JsonDocument.Parse(marker);
            var root = document.RootElement;
            if (!root.TryGetProperty("expiresUtc", out var expiry) ||
                !DateTimeOffset.TryParse(expiry.GetString(), CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var expires) || expires <= now) return null;
            var directory = Path.GetFullPath(root.GetProperty("directory").GetString()!);
            var parent = Path.GetFullPath(diagnosticsDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!directory.StartsWith(parent, StringComparison.OrdinalIgnoreCase)) return null;
            return new(directory, expires < now.AddMinutes(15) ? expires : now.AddMinutes(15));
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or
            KeyNotFoundException or ArgumentException or NotSupportedException or IOException)
        { return null; }
    }
}
