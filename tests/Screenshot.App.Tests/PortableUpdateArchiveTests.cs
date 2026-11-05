using System.IO;
using System.IO.Compression;
using System.Text;
using SharpCompress.Common;
using SharpCompress.Writers.SevenZip;
using Screenshot.App.Update;

namespace Screenshot.App.Tests;

public sealed class PortableUpdateArchiveTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "SnapCut-update-test-" + Guid.NewGuid().ToString("N"));

    public PortableUpdateArchiveTests() => Directory.CreateDirectory(_root);

    [Theory]
    [InlineData("zip")]
    [InlineData("7z")]
    public void ExtractsRunnerAndUpdatesWhilePreservingUserData(string format)
    {
        var package = CreatePackage(format, ("SnapCut.exe", "new executable"),
            ("native/engine.dll", "new engine"), ("ScreenshotData/settings.json", "unwanted"),
            ("TranslationModels/model.bin", "unwanted"));
        var runner = Path.Combine(_root, "runner.exe");
        PortableUpdateArchive.ExtractExecutable(package, runner);
        Assert.Equal("new executable", File.ReadAllText(runner));
        var target = Path.Combine(_root, "app");
        Directory.CreateDirectory(Path.Combine(target, "ScreenshotData"));
        Directory.CreateDirectory(Path.Combine(target, "TranslationModels"));
        File.WriteAllText(Path.Combine(target, "SnapCut.exe"), "old executable");
        File.WriteAllText(Path.Combine(target, "ScreenshotData/settings.json"), "personal");
        File.WriteAllText(Path.Combine(target, "TranslationModels/model.bin"), "downloaded");
        PortableUpdateRunner.ApplyPackage(package, target);
        Assert.Equal("new executable", File.ReadAllText(Path.Combine(target, "SnapCut.exe")));
        Assert.Equal("new engine", File.ReadAllText(Path.Combine(target, "native/engine.dll")));
        Assert.Equal("personal", File.ReadAllText(Path.Combine(target, "ScreenshotData/settings.json")));
        Assert.Equal("downloaded", File.ReadAllText(Path.Combine(target, "TranslationModels/model.bin")));
        Assert.Empty(Directory.GetDirectories(_root, "apply-*"));
    }

    [Theory]
    [InlineData("zip")]
    [InlineData("7z")]
    public void WriteFailureRestoresAlreadyReplacedFiles(string format)
    {
        var package = CreatePackage(format, ("SnapCut.exe", "new"), ("a.dll", "new dependency"), ("z.dll", "locked"));
        var target = Path.Combine(_root, "app");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "SnapCut.exe"), "old");
        File.WriteAllText(Path.Combine(target, "a.dll"), "old dependency");
        File.WriteAllText(Path.Combine(target, "z.dll"), "old locked");
        using var locked = File.Open(Path.Combine(target, "z.dll"), FileMode.Open, FileAccess.Read, FileShare.None);
        Assert.ThrowsAny<IOException>(() => PortableUpdateRunner.ApplyPackage(package, target));
        Assert.Equal("old", File.ReadAllText(Path.Combine(target, "SnapCut.exe")));
        Assert.Equal("old dependency", File.ReadAllText(Path.Combine(target, "a.dll")));
    }

    [Theory]
    [InlineData("zip")]
    [InlineData("7z")]
    public void MissingExecutableLeavesInstallationUntouched(string format)
    {
        var package = CreatePackage(format, ("a.dll", "new"));
        var target = Path.Combine(_root, "app");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "SnapCut.exe"), "old");
        Assert.Throws<InvalidDataException>(() => PortableUpdateRunner.ApplyPackage(package, target));
        Assert.Equal("old", File.ReadAllText(Path.Combine(target, "SnapCut.exe")));
        Assert.False(File.Exists(Path.Combine(target, "a.dll")));
    }

    [Theory]
    [InlineData("zip")]
    [InlineData("7z")]
    public void TruncatedArchiveLeavesInstallationUntouched(string format)
    {
        var package = CreatePackage(format, ("SnapCut.exe", "new"), ("a.dll", "new dependency"));
        using (var file = File.OpenWrite(package)) file.SetLength(new FileInfo(package).Length / 2);
        var target = Path.Combine(_root, "app");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "SnapCut.exe"), "old");
        Assert.NotNull(Record.Exception(() => PortableUpdateRunner.ApplyPackage(package, target)));
        Assert.Equal("old", File.ReadAllText(Path.Combine(target, "SnapCut.exe")));
        Assert.False(File.Exists(Path.Combine(target, "a.dll")));
    }

    [Theory]
    [InlineData("../outside.exe")]
    [InlineData("/outside.exe")]
    [InlineData("C:/outside.exe")]
    [InlineData("native/../outside.exe")]
    [InlineData("SnapCut.exe:stream")]
    public void RejectsUnsafePathsBeforeExtractingRunner(string name)
    {
        var package = CreatePackage("zip", ("SnapCut.exe", "new"), (name, "unsafe"));
        var runner = Path.Combine(_root, "runner.exe");
        Assert.Throws<InvalidDataException>(() => PortableUpdateArchive.ExtractExecutable(package, runner));
        Assert.False(File.Exists(runner));
    }

    private string CreatePackage(string format, params (string Name, string Content)[] files)
    {
        var path = Path.Combine(_root, "update." + format);
        if (format == "zip")
        {
            using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
            foreach (var file in files)
            {
                using var writer = new StreamWriter(zip.CreateEntry(file.Name).Open());
                writer.Write(file.Content);
            }
        }
        else
        {
            using var writer = SevenZipWriter.OpenWriter(path, new SevenZipWriterOptions(CompressionType.LZMA2));
            foreach (var file in files)
            {
                using var input = new MemoryStream(Encoding.UTF8.GetBytes(file.Content));
                writer.Write(file.Name, input, DateTime.UtcNow);
            }
        }
        return path;
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
