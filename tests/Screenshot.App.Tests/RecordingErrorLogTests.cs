using System.IO;
using Screenshot.App.Capture;

namespace Screenshot.App.Tests;

public sealed class RecordingErrorLogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "SnapCut-recording-errors", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ConsecutiveFailuresAreOrderedFlushedAndRetainedAcrossInstances()
    {
        var log = new RecordingErrorLog(_root, _root);
        _ = log.WriteAsync("startup-attempt-1", "H265 / 60 fps", "0xc00d4a3e");
        _ = log.WriteAsync("startup-attempt-2", "software", "codec not supported");
        var saved = await log.FlushAsync();
        Assert.Null(saved.Error);
        var another = await new RecordingErrorLog(_root, _root).WriteAsync("open-recording", "H264", "invalid directory");
        Assert.Equal(saved.FilePath, another.FilePath);
        var text = await File.ReadAllTextAsync(saved.FilePath!);
        Assert.True(text.IndexOf("startup-attempt-1", StringComparison.Ordinal) < text.IndexOf("startup-attempt-2", StringComparison.Ordinal));
        Assert.Contains("0xc00d4a3e", text);
        Assert.Contains("invalid directory", text);
    }

    [Fact]
    public async Task UnwritableApplicationDirectoryFallsBackAndReportsActualPath()
    {
        Directory.CreateDirectory(_root);
        var blocked = Path.Combine(_root, "file-instead-of-directory");
        await File.WriteAllTextAsync(blocked, "keep");
        var fallback = Path.Combine(_root, "fallback");
        var saved = await new RecordingErrorLog(blocked, fallback).WriteAsync("initialization", "H264", "failure");
        Assert.Equal(Path.Combine(fallback, RecordingErrorLog.FileName), saved.FilePath);
        Assert.Contains(saved.FilePath!, saved.Description);
        Assert.Contains("Primary log unavailable", await File.ReadAllTextAsync(saved.FilePath!));
        var unsaved = await new RecordingErrorLog(blocked, blocked).WriteAsync("initialization", "H264", "failure");
        Assert.Null(unsaved.FilePath);
        Assert.Contains("未能保存", unsaved.Description);
        Assert.Equal("keep", await File.ReadAllTextAsync(blocked));
    }

    [Fact]
    public async Task RotationKeepsOnePreviousFileAndNoIdleWriter()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, RecordingErrorLog.FileName);
        await File.WriteAllTextAsync(path, new string('a', RecordingErrorLog.MaximumFileBytes));
        var log = new RecordingErrorLog(_root, _root);
        await log.WriteAsync("first", "context", "current error");
        Assert.Equal(RecordingErrorLog.MaximumFileBytes, new FileInfo(path + ".previous").Length);
        Assert.Contains("current error", await File.ReadAllTextAsync(path));
        Assert.True(log.FlushAsync().IsCompletedSuccessfully);
        Assert.Equal(2, Directory.GetFiles(_root).Length);
    }

    public void Dispose()
    {
        if (!Directory.Exists(_root)) return;
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)) File.Delete(file);
        foreach (var directory in Directory.EnumerateDirectories(_root)) Directory.Delete(directory);
        Directory.Delete(_root);
    }
}
