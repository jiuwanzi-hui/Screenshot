using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text.Json;
using Screenshot.App.Capture;
using Screenshot.App.Core;

namespace Screenshot.App.Pin;

internal sealed record PinnedImageState(
    string Id,
    string ImageFileName,
    double Left,
    double Top,
    double Width,
    double Height);

internal sealed class PinnedImagePersistenceStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private readonly string _directory;
    private readonly string _indexPath;

    public PinnedImagePersistenceStore(string? directory = null)
    {
        _directory = directory ?? AppMetadata.PinnedImagesDirectoryPath;
        _indexPath = Path.Combine(_directory, "index.json");
    }

    public IReadOnlyList<PinnedImageState> Load()
    {
        try
        {
            if (!File.Exists(_indexPath))
            {
                return [];
            }

            var states = JsonSerializer.Deserialize<PinnedImageState[]>(
                File.ReadAllText(_indexPath), JsonOptions) ?? [];
            return states.Where(state =>
                    !string.IsNullOrWhiteSpace(state.Id) &&
                    !string.IsNullOrWhiteSpace(state.ImageFileName) &&
                    File.Exists(GetImagePath(state.ImageFileName)))
                .ToArray();
        }
        catch (IOException)
        {
            return [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public CapturedImage? LoadImage(PinnedImageState state)
    {
        try
        {
            using var loaded = new Bitmap(GetImagePath(state.ImageFileName));
            return new CapturedImage(new Bitmap(loaded));
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    public void SaveImage(string id, CapturedImage image)
    {
        Directory.CreateDirectory(_directory);
        var path = GetImagePath(GetImageFileName(id));
        var temporaryPath = path + ".tmp";
        using (var copy = new Bitmap(image.Bitmap))
        {
            copy.Save(temporaryPath, ImageFormat.Png);
        }
        File.Move(temporaryPath, path, overwrite: true);
    }

    public void SaveIndex(IEnumerable<PinnedImageState> states)
    {
        Directory.CreateDirectory(_directory);
        var json = JsonSerializer.Serialize(states.ToArray(), JsonOptions);
        var temporaryPath = _indexPath + ".tmp";
        File.WriteAllText(temporaryPath, json);
        File.Move(temporaryPath, _indexPath, overwrite: true);
    }

    public void Delete(string id)
    {
        try
        {
            File.Delete(GetImagePath(GetImageFileName(id)));
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    internal static string GetImageFileName(string id) => $"{id}.png";

    private string GetImagePath(string fileName) =>
        Path.Combine(_directory, Path.GetFileName(fileName));
}
