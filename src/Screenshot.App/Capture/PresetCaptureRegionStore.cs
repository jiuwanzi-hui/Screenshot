using System.IO;
using System.Text.Json;
using Screenshot.App.Core;

namespace Screenshot.App.Capture;

public sealed class PresetCaptureRegionStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
    };

    private readonly string _path = Path.Combine(
        AppMetadata.DataDirectoryPath,
        "preset-capture-regions.json");

    public IReadOnlyList<ScreenRegion> Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return [];
            }

            var regions = JsonSerializer.Deserialize<List<ScreenRegion>>(
                File.ReadAllText(_path),
                SerializerOptions);
            return regions?
                .Where(region => !region.IsEmpty)
                .Take(5)
                .ToArray() ?? [];
        }
        catch (Exception exception) when (
            exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public void Save(IEnumerable<ScreenRegion> regions)
    {
        var normalized = regions
            .Where(region => !region.IsEmpty)
            .Take(5)
            .ToArray();
        var directory = Path.GetDirectoryName(_path)
            ?? AppMetadata.DataDirectoryPath;
        Directory.CreateDirectory(directory);
        var temporaryPath = _path + ".tmp";
        try
        {
            File.WriteAllText(
                temporaryPath,
                JsonSerializer.Serialize(normalized, SerializerOptions));
            File.Move(temporaryPath, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}
