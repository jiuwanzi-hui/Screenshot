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

    public string HistoryDirectory { get; }

    public string Save(PixelImage image, bool isScrollCapture)
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
}
