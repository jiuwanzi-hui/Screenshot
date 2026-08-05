using System.Globalization;

namespace Screenshot.App.Capture;

public sealed record VideoHistoryItem(
    string FilePath,
    string FileName,
    DateTimeOffset RecordedAt,
    long FileSizeBytes)
{
    public string FileSizeText => FormatFileSize(FileSizeBytes);

    private static string FormatFileSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var size = Math.Max(0, bytes);
        var unitIndex = 0;
        var displaySize = (double)size;
        while (displaySize >= 1024 && unitIndex < units.Length - 1)
        {
            displaySize /= 1024;
            unitIndex++;
        }

        var format = unitIndex == 0 ? "0" : "0.##";
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{displaySize.ToString(format, CultureInfo.InvariantCulture)} {units[unitIndex]}");
    }
}
