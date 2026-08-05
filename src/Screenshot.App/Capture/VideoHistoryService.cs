using System.Collections.ObjectModel;
using System.IO;

namespace Screenshot.App.Capture;

public enum VideoHistorySortMode
{
    NewestFirst,
    OldestFirst,
    FileName,
    LargestFirst,
}

public sealed class VideoHistoryService
{
    public ObservableCollection<VideoHistoryItem> Items { get; } = [];

    public void Refresh(
        string videoDirectory,
        VideoHistorySortMode sortMode = VideoHistorySortMode.NewestFirst)
    {
        Items.Clear();
        if (string.IsNullOrWhiteSpace(videoDirectory) ||
            !Directory.Exists(videoDirectory))
        {
            return;
        }

        try
        {
            var files = Directory
                .EnumerateFiles(
                    videoDirectory,
                    "*.mp4",
                    SearchOption.TopDirectoryOnly)
                .Select(path => new FileInfo(path))
                .Where(file => file.Exists);
            var recordings = ApplySort(files, sortMode)
                .Select(file => new VideoHistoryItem(
                    file.FullName,
                    file.Name,
                    new DateTimeOffset(file.LastWriteTime),
                    file.Length));

            foreach (var recording in recordings)
            {
                Items.Add(recording);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public static string Rename(VideoHistoryItem item, string requestedName)
    {
        ArgumentNullException.ThrowIfNull(item);

        var sourcePath = Path.GetFullPath(item.FilePath);
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("录屏文件不存在。", sourcePath);
        }

        var baseName = NormalizeFileName(requestedName);
        var destinationPath = Path.Combine(
            Path.GetDirectoryName(sourcePath)
                ?? throw new IOException("无法确定录屏文件所在目录。"),
            $"{baseName}{Path.GetExtension(sourcePath)}");
        if (string.Equals(
                sourcePath,
                destinationPath,
                StringComparison.OrdinalIgnoreCase))
        {
            return sourcePath;
        }

        if (File.Exists(destinationPath))
        {
            throw new IOException("同名录屏文件已经存在。");
        }

        File.Move(sourcePath, destinationPath);
        return destinationPath;
    }

    public bool Delete(VideoHistoryItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        File.Delete(item.FilePath);
        return Items.Remove(item);
    }

    internal static string NormalizeFileName(string requestedName)
    {
        var name = (requestedName ?? string.Empty).Trim();
        if (name.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^4].TrimEnd();
        }

        name = name.TrimEnd('.', ' ');
        if (name.Length == 0)
        {
            throw new ArgumentException("请输入视频名称。", nameof(requestedName));
        }

        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ArgumentException(
                "视频名称不能包含 \\ / : * ? \" < > |。",
                nameof(requestedName));
        }

        return name;
    }

    private static IOrderedEnumerable<FileInfo> ApplySort(
        IEnumerable<FileInfo> files,
        VideoHistorySortMode sortMode)
    {
        return sortMode switch
        {
            VideoHistorySortMode.OldestFirst => files
                .OrderBy(file => file.LastWriteTimeUtc)
                .ThenBy(file => file.Name, StringComparer.OrdinalIgnoreCase),
            VideoHistorySortMode.FileName => files
                .OrderBy(file => file.Name, StringComparer.OrdinalIgnoreCase),
            VideoHistorySortMode.LargestFirst => files
                .OrderByDescending(file => file.Length)
                .ThenByDescending(file => file.LastWriteTimeUtc),
            _ => files
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .ThenBy(file => file.Name, StringComparer.OrdinalIgnoreCase),
        };
    }
}
