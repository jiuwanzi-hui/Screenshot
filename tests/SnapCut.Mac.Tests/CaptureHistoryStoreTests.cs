using SnapCut.Mac.App;

namespace SnapCut.Mac.Tests;

public sealed class CaptureHistoryStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"SnapCut-Mac-History-{Guid.NewGuid():N}");

    [Fact]
    public void ReturnsNewestPngFilesAcrossDayFolders()
    {
        var first = Create("2026-08-01", "first.png", new DateTime(2026, 8, 1));
        var newest = Create("2026-08-03", "newest.png", new DateTime(2026, 8, 3));
        _ = Create("2026-08-02", "ignored.txt", new DateTime(2026, 8, 2));
        var second = Create("2026-08-02", "second.png", new DateTime(2026, 8, 2));
        var store = new CaptureHistoryStore(_directory);

        var result = store.GetRecent(2);

        Assert.Equal([newest, second], result);
        Assert.DoesNotContain(first, result);
    }

    [Fact]
    public void TrimToLimitDeletesOnlyOldPngFilesAndEmptyFolders()
    {
        var oldest = Create("2026-08-01", "oldest.png", new DateTime(2026, 8, 1));
        var middle = Create("2026-08-02", "middle.png", new DateTime(2026, 8, 2));
        var newest = Create("2026-08-03", "newest.png", new DateTime(2026, 8, 3));
        var unrelated = Create("2026-08-01", "keep.txt", new DateTime(2026, 8, 1));
        var store = new CaptureHistoryStore(_directory);

        store.TrimToLimit(2);

        Assert.False(File.Exists(oldest));
        Assert.True(File.Exists(middle));
        Assert.True(File.Exists(newest));
        Assert.True(File.Exists(unrelated));
    }

    [Fact]
    public void UpdateDirectoryChangesTheActiveSaveLocation()
    {
        var next = Path.Combine(_directory, "next");
        var store = new CaptureHistoryStore(_directory);

        store.UpdateDirectory(next);

        Assert.Equal(Path.GetFullPath(next), store.HistoryDirectory);
    }

    private string Create(string folder, string name, DateTime modified)
    {
        var directory = Path.Combine(_directory, folder);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, name);
        File.SetLastWriteTimeUtc(path, modified.ToUniversalTime());
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
