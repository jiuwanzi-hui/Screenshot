using SnapCut.Core;
using SnapCut.Mac.Capture;

namespace SnapCut.Mac.App;

internal sealed class CaptureHistoryStore
{
    public CaptureHistoryStore(string? historyDirectory = null)
    {
        HistoryDirectory = historyDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            "SnapCut");
    }

    public string HistoryDirectory { get; private set; }

    public void UpdateDirectory(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        HistoryDirectory = Path.GetFullPath(directory.Trim());
    }

    public string Save(
        PixelImage image,
        bool isScrollCapture,
        int historyLimit)
    {
        ArgumentNullException.ThrowIfNull(image);
        var now = DateTime.Now;
        var directory = Path.Combine(HistoryDirectory, now.ToString("yyyy-MM-dd"));
        Directory.CreateDirectory(directory);
        var kind = isScrollCapture ? "Long" : "Capture";
        var path = Path.Combine(
            directory,
            $"SnapCut-{kind}-{now:yyyyMMdd-HHmmss-fff}.png");
        MacScreenCaptureService.SavePng(image, path);
        TrimToLimit(historyLimit);
        return path;
    }

    public IReadOnlyList<string> GetRecent(int limit)
    {
        if (!Directory.Exists(HistoryDirectory) || limit <= 0)
        {
            return [];
        }

        try
        {
            return Directory
                .EnumerateFiles(HistoryDirectory, "*.png", SearchOption.AllDirectories)
                .Select(path => new FileInfo(path))
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .Take(limit)
                .Select(file => file.FullName)
                .ToArray();
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    public void TrimToLimit(int limit)
    {
        if (!Directory.Exists(HistoryDirectory))
        {
            return;
        }

        limit = Math.Clamp(limit, 1, 100);
        try
        {
            var expired = Directory
                .EnumerateFiles(HistoryDirectory, "*.png", SearchOption.AllDirectories)
                .Select(path => new FileInfo(path))
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .Skip(limit)
                .ToArray();
            foreach (var file in expired)
            {
                try
                {
                    file.Delete();
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }

            foreach (var directory in Directory
                         .EnumerateDirectories(
                             HistoryDirectory,
                             "*",
                             SearchOption.AllDirectories)
                         .OrderByDescending(path => path.Length))
            {
                try
                {
                    if (!Directory.EnumerateFileSystemEntries(directory).Any())
                    {
                        Directory.Delete(directory);
                    }
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
