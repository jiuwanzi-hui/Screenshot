using System.IO;

namespace Screenshot.App.Text;

internal sealed record OcrModelUpgradeResult(bool NeedsReminder, string? CleanupError);

// Preserve installed models. Medium-only users need the Small files once;
// fresh installs and already usable Small installs do not get a startup prompt.
internal static class OcrModelUpgrade
{
    private static string Marker(string dataDirectory) =>
        Path.Combine(dataDirectory, "ModelUpgrades", "ppocrv6-fast-artistic-v2.txt");

    internal static OcrModelUpgradeResult Prepare(string modelRoot, string dataDirectory)
    {
        using var manager = new HighQualityOcrModelManager(HighQualityOcrModelManager.ResolveInstallationDirectory(modelRoot));
        var status = manager.GetStatus();
        if (status.IsInstalled || !status.HasArtisticModel) return new(false, null);
        var marker = Marker(dataDirectory);
        return new(!File.Exists(marker) || File.ReadAllText(marker).Trim() != "notified", null);
    }

    internal static void MarkNotified(string dataDirectory)
    {
        var path = Marker(dataDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, "notified");
        File.Move(temporary, path, overwrite: true);
    }
}
