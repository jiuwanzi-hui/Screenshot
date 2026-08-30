using System.Globalization;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace Screenshot.App.Capture;

public sealed class VideoHistoryItem : INotifyPropertyChanged
{
    private ImageSource? _thumbnail;
    private TimeSpan? _duration;
    private int _pixelWidth;
    private int _pixelHeight;
    private bool _metadataReadFailed;

    public VideoHistoryItem(
        string filePath,
        string fileName,
        DateTimeOffset recordedAt,
        long fileSizeBytes)
    {
        FilePath = filePath;
        FileName = fileName;
        RecordedAt = recordedAt;
        FileSizeBytes = fileSizeBytes;
    }

    public string FilePath { get; }

    public string FileName { get; }

    public DateTimeOffset RecordedAt { get; }

    public long FileSizeBytes { get; }

    public TimeSpan? Duration
    {
        get => _duration;
        private set
        {
            if (_duration == value)
            {
                return;
            }

            _duration = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DurationText));
        }
    }

    public int PixelWidth
    {
        get => _pixelWidth;
        private set
        {
            if (_pixelWidth == value)
            {
                return;
            }

            _pixelWidth = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ResolutionText));
        }
    }

    public int PixelHeight
    {
        get => _pixelHeight;
        private set
        {
            if (_pixelHeight == value)
            {
                return;
            }

            _pixelHeight = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ResolutionText));
        }
    }

    public ImageSource? Thumbnail
    {
        get => _thumbnail;
        set
        {
            if (ReferenceEquals(_thumbnail, value))
            {
                return;
            }

            _thumbnail = value;
            OnPropertyChanged();
        }
    }

    public string FileSizeText => FormatFileSize(FileSizeBytes);

    public string DurationText => Duration is { } duration && duration >= TimeSpan.Zero
        ? FormatDuration(duration)
        : _metadataReadFailed ? "时长读取失败" : "时长读取中…";

    public string ResolutionText => PixelWidth > 0 && PixelHeight > 0
        ? $"{PixelWidth} × {PixelHeight}"
        : _metadataReadFailed ? "分辨率读取失败" : "分辨率读取中…";

    public string FormatText =>
        Path.GetExtension(FilePath).TrimStart('.').ToUpperInvariant() switch
        {
            "" => "视频",
            var extension => extension,
        };

    public void SetMediaMetadata(TimeSpan? duration, int pixelWidth, int pixelHeight)
    {
        _metadataReadFailed = false;
        Duration = duration;
        PixelWidth = Math.Max(0, pixelWidth);
        PixelHeight = Math.Max(0, pixelHeight);
    }

    public void MarkMediaMetadataReadFailed()
    {
        if (_metadataReadFailed)
        {
            return;
        }

        _metadataReadFailed = true;
        OnPropertyChanged(nameof(DurationText));
        OnPropertyChanged(nameof(ResolutionText));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

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

    private static string FormatDuration(TimeSpan duration)
    {
        return duration.TotalHours >= 1
            ? duration.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture)
            : duration.ToString(@"mm\:ss", CultureInfo.InvariantCulture);
    }
}
