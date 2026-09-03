using System.Text.Json;

namespace SnapCut.Mac.Pin;

internal sealed class MacPinnedImagePersistenceStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    public MacPinnedImagePersistenceStore(string? path = null)
    {
        Path = path ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Library",
            "Application Support",
            "SnapCut",
            "pinned-images.json");
    }

    public string Path { get; }

    public IReadOnlyList<MacPinnedImageState> Load()
    {
        try
        {
            if (!File.Exists(Path))
            {
                return [];
            }

            return (JsonSerializer.Deserialize<MacPinnedImageState[]>(
                        File.ReadAllText(Path),
                        JsonOptions) ?? [])
                .Where(state => File.Exists(state.ImagePath))
                .Take(30)
                .ToArray();
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    public void Save(IEnumerable<MacPinnedImageState> states)
    {
        var directory = System.IO.Path.GetDirectoryName(Path)
            ?? throw new InvalidOperationException("无法确定钉图状态目录。");
        Directory.CreateDirectory(directory);
        var temporary = Path + ".tmp";
        File.WriteAllText(
            temporary,
            JsonSerializer.Serialize(states.Take(30), JsonOptions));
        File.Move(temporary, Path, overwrite: true);
    }
}
