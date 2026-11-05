using System.IO;
using Screenshot.App.Text;

namespace Screenshot.App.Tests;

public sealed class OcrModelUpgradeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "SnapCut-OcrUpgrade-" + Guid.NewGuid().ToString("N"));
    private string Models => Path.Combine(_root, "models");
    private string Data => Path.Combine(_root, "data");

    [Fact]
    public void FreshInstallationDoesNotCreateUpgradeReminderOrDirectories()
    {
        Assert.False(OcrModelUpgrade.Prepare(Models, Data).NeedsReminder);
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public void ExistingSmallRemainsUsableAndIsNeverDeleted()
    {
        var path = Path.Combine(Models, "PP-OCRv6-Small");
        Install(path, small: true);
        Directory.CreateDirectory(Path.Combine(Models, "PP-OCRv6-Medium")); // Abandoned download.
        File.WriteAllText(Path.Combine(path, "personal.txt"), "keep");
        Assert.False(OcrModelUpgrade.Prepare(Models, Data).NeedsReminder);
        Assert.Equal(path, HighQualityOcrModelManager.ResolveInstallationDirectory(Models));
        using var manager = new HighQualityOcrModelManager(path);
        Assert.True(manager.GetStatus().IsInstalled);
        Assert.False(manager.GetStatus().HasArtisticModel);
        Assert.Equal("keep", File.ReadAllText(Path.Combine(path, "personal.txt")));
    }

    [Fact]
    public void MediumOnlyUserGetsOneReminderAndTheInstalledModelIsPreserved()
    {
        var path = Path.Combine(Models, "PP-OCRv6-Medium");
        Install(path, small: false);
        Assert.True(OcrModelUpgrade.Prepare(Models, Data).NeedsReminder);
        Assert.True(OcrModelUpgrade.Prepare(Models, Data).NeedsReminder);
        using var manager = new HighQualityOcrModelManager(path);
        Assert.False(manager.GetStatus().IsInstalled);
        Assert.True(manager.GetStatus().HasArtisticModel);
        Assert.Equal(31_163_977, manager.GetStatus().DownloadSize);
        OcrModelUpgrade.MarkNotified(Data);
        Assert.False(OcrModelUpgrade.Prepare(Models, Data).NeedsReminder);
        Assert.True(File.Exists(manager.ArtisticRecognitionModelPath));
        Install(path, small: true);
        Assert.True(manager.GetStatus().IsInstalled);
        Assert.Equal(0, manager.GetStatus().DownloadSize);
    }

    [Fact]
    public void OldCleanupMarkerCannotDeleteRestoredSmallOrTriggerTheOldReminder()
    {
        var path = Path.Combine(Models, "PP-OCRv6-Small");
        Install(path, small: true);
        Directory.CreateDirectory(Path.Combine(Data, "ModelUpgrades"));
        File.WriteAllText(Path.Combine(Data, "ModelUpgrades", "ppocrv6-medium-v1.txt"), "pending");
        Assert.False(OcrModelUpgrade.Prepare(Models, Data).NeedsReminder);
        Assert.True(File.Exists(Path.Combine(path, "PP-OCRv6_rec_small.onnx")));
    }

    private static void Install(string path, bool small)
    {
        Directory.CreateDirectory(path);
        (string, long)[] files = [(small ? "PP-OCRv6_det_small.onnx" : "PP-OCRv6_det_medium.onnx", small ? 9_929_594 : 62_119_454),
            (small ? "PP-OCRv6_rec_small.onnx" : "PP-OCRv6_rec_medium.onnx", small ? 21_234_383 : 76_629_984),
            ("ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx", 1_018_508), ("ppocrv6_dict.txt", 74_947)];
        foreach (var (name, size) in files) { using var file = File.Create(Path.Combine(path, name)); file.SetLength(size); }
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
