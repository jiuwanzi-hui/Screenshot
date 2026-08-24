using System.IO;
using Screenshot.App.Capture;
using Screenshot.App.Core;

namespace Screenshot.App.Tests;

public sealed class HistoryRetentionTests
{
    [Fact]
    public void NormalizesRetentionDaysAndHistoryLimits()
    {
        var normalized = (AppSettings.CreateDefault() with
        {
            ScreenshotHistoryRetentionDays = 14,
            VideoHistoryRetentionDays = -1,
            HistoryLimit = 0,
            VideoHistoryLimit = 200,
        }).Normalize();

        Assert.Equal(14, normalized.ScreenshotHistoryRetentionDays);
        Assert.Equal(0, normalized.VideoHistoryRetentionDays);
        Assert.Equal(1, normalized.HistoryLimit);
        Assert.Equal(100, normalized.VideoHistoryLimit);
    }

    [Fact]
    public void MigratesEarlierHistorySettingsToNewDefaults()
    {
        var normalized = (AppSettings.CreateDefault() with
        {
            SettingsVersion = 10,
            ScreenshotHistoryRetentionDays = 0,
            VideoHistoryRetentionDays = 0,
            HistoryLimit = 100,
            VideoHistoryLimit = 1,
        }).Normalize();

        Assert.Equal(13, normalized.SettingsVersion);
        Assert.Equal(7, normalized.ScreenshotHistoryRetentionDays);
        Assert.Equal(7, normalized.VideoHistoryRetentionDays);
        Assert.Equal(50, normalized.HistoryLimit);
        Assert.Equal(50, normalized.VideoHistoryLimit);
    }

    [Fact]
    public void PrunesScreenshotCacheByAge()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var oldFile = Path.Combine(directory, "history-old.png");
            var recentFile = Path.Combine(directory, "history-recent.png");
            File.WriteAllText(oldFile, "old");
            File.WriteAllText(recentFile, "recent");
            var now = new DateTimeOffset(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);
            File.SetLastWriteTimeUtc(oldFile, now.UtcDateTime.AddDays(-8));
            File.SetLastWriteTimeUtc(recentFile, now.UtcDateTime.AddDays(-2));

            CaptureHistoryService.PruneCacheDirectoryByAge(7, directory, now);

            Assert.False(File.Exists(oldFile));
            Assert.True(File.Exists(recentFile));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void PrunesVideoHistoryByAgeWithoutTouchingOtherFiles()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var oldVideo = Path.Combine(directory, "old.mp4");
            var recentVideo = Path.Combine(directory, "recent.mp4");
            var oldText = Path.Combine(directory, "notes.txt");
            File.WriteAllText(oldVideo, "old");
            File.WriteAllText(recentVideo, "recent");
            File.WriteAllText(oldText, "keep");
            var now = new DateTimeOffset(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);
            File.SetLastWriteTimeUtc(oldVideo, now.UtcDateTime.AddDays(-31));
            File.SetLastWriteTimeUtc(recentVideo, now.UtcDateTime.AddDays(-2));
            File.SetLastWriteTimeUtc(oldText, now.UtcDateTime.AddDays(-31));

            VideoHistoryService.ApplyRetentionPolicy(directory, 30, 100, now);

            Assert.False(File.Exists(oldVideo));
            Assert.True(File.Exists(recentVideo));
            Assert.True(File.Exists(oldText));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void VideoRetentionDeletesOldestFilesWhenCapacityIsExceeded()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var now = new DateTimeOffset(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);
            for (var index = 0; index < 4; index++)
            {
                var path = Path.Combine(directory, $"video-{index}.mp4");
                File.WriteAllText(
                    path,
                    index.ToString(System.Globalization.CultureInfo.InvariantCulture));
                File.SetLastWriteTimeUtc(path, now.UtcDateTime.AddMinutes(index));
            }

            VideoHistoryService.ApplyRetentionPolicy(directory, 0, 2, now);

            var remaining = Directory.EnumerateFiles(directory, "*.mp4")
                .Select(Path.GetFileName)
                .OrderBy(name => name)
                .ToArray();
            Assert.Equal(["video-2.mp4", "video-3.mp4"], remaining);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "Screenshot.App.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}
