using System.IO;
using Screenshot.App;

namespace Screenshot.App.Tests;

public sealed class CrashLogTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "Screenshot.App.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void AppendCrashLogRestartsTheLogWhenItWouldExceedTenMiB()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "crash.log");
        using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write))
        {
            stream.SetLength(App.MaximumCrashLogSizeBytes);
        }

        const string latestEntry = "latest crash";
        App.AppendCrashLog(path, latestEntry);

        Assert.Equal(latestEntry, File.ReadAllText(path));
        Assert.Equal(latestEntry.Length, new FileInfo(path).Length);
    }

    [Fact]
    public void AppendCrashLogAppendsBelowTheSizeLimit()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "crash.log");
        File.WriteAllText(path, "first\n");

        App.AppendCrashLog(path, "second\n");

        Assert.Equal("first\nsecond\n", File.ReadAllText(path));
    }

    [Fact]
    public void ExistingOversizedCrashLogIsClearedWhenTheApplicationStarts()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "crash.log");
        File.WriteAllText(path, "existing log");
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write))
        {
            stream.SetLength(App.MaximumCrashLogSizeBytes + 1);
        }

        App.TrimCrashLogIfOversized(path);

        Assert.Empty(File.ReadAllText(path));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
