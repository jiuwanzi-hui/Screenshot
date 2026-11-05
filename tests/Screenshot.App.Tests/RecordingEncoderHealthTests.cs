using System.IO;
using System.Text.Json;
using Screenshot.App.Capture;
using Screenshot.App.Core;

namespace Screenshot.App.Tests;

public sealed class RecordingEncoderHealthTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "SnapCut-encoder-health", Guid.NewGuid().ToString("N"));

    [Fact]
    public void ObservedFailureSurvivesRestartAndIsScopedToDisplayCodecAndLifetime()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "health.json");
        var key = RecordingEncoderHealth.CreateKey("physical-intel", VideoRecordingCodec.H264);
        new RecordingEncoderHealth(path).RememberFailure(key);
        var reloaded = new RecordingEncoderHealth(path);
        Assert.True(reloaded.HasRecentFailure(key, "DISPLAY1", VideoRecordingCodec.H264));
        Assert.False(reloaded.HasRecentFailure(RecordingEncoderHealth.CreateKey("physical-intel", VideoRecordingCodec.H265), "DISPLAY1", VideoRecordingCodec.H265));
        Assert.False(reloaded.HasRecentFailure(RecordingEncoderHealth.CreateKey("other-output", VideoRecordingCodec.H264), "DISPLAY2", VideoRecordingCodec.H264));
        File.WriteAllText(path, JsonSerializer.Serialize(new Dictionary<string, DateTimeOffset>
            { [key] = DateTimeOffset.UtcNow - RecordingEncoderHealth.Lifetime - TimeSpan.FromMinutes(1) }));
        Assert.False(reloaded.HasRecentFailure(key, "DISPLAY1", VideoRecordingCodec.H264));
        File.WriteAllText(path, "broken cache");
        Assert.False(reloaded.HasRecentFailure(key, "DISPLAY1", VideoRecordingCodec.H264));
    }

    [Fact]
    public void Existing402DiagnosticAvoidsRepeatingTheSameHardwareFailure()
    {
        Directory.CreateDirectory(_directory);
        var diagnostic = Path.Combine(_directory, "recording-error.log");
        var lines = $"OS: {Environment.OSVersion.VersionString}\nDisplay: DISPLAY1\nCapture: 1920x1080, H264, 30 fps\nMode: 硬件加速录制\nWrite error (0xc00d4a3e) in video encoder";
        File.WriteAllText(diagnostic, lines);
        var health = new RecordingEncoderHealth(Path.Combine(_directory, "health.json"));
        Assert.True(health.HasRecentFailure("same-output", "DISPLAY1", VideoRecordingCodec.H264));
        Assert.False(health.HasRecentFailure("other-output", "DISPLAY2", VideoRecordingCodec.H264));
        Assert.False(health.HasRecentFailure("other-codec", "DISPLAY1", VideoRecordingCodec.H265));
        File.WriteAllText(diagnostic, lines.Replace("Mode: 硬件加速录制", "Mode: 兼容录制（软件编码）", StringComparison.Ordinal));
        Assert.False(health.HasRecentFailure("same-output", "DISPLAY1", VideoRecordingCodec.H264));
    }

    [Theory]
    [InlineData("Write error (0xc00d4a3e) in video encoder", true)]
    [InlineData("0xC00D4A3E", true)]
    [InlineData("Write error in audio encoder", false)]
    [InlineData("磁盘空间不足", false)]
    public void OnlyVideoEncoderFailuresDisableHardware(string error, bool expected) =>
        Assert.Equal(expected, RecordingEncoderHealth.IsEncoderFailure(error));

    public void Dispose()
    {
        if (!Directory.Exists(_directory)) return;
        foreach (var file in Directory.EnumerateFiles(_directory)) File.Delete(file);
        Directory.Delete(_directory);
    }
}
