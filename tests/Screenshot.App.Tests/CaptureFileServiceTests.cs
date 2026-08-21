using System.IO;
using Screenshot.App.Capture;

namespace Screenshot.App.Tests;

public sealed class CaptureFileServiceTests : IDisposable
{
    private readonly string _testDirectory;

    public CaptureFileServiceTests()
    {
        _testDirectory = Path.Combine(
            Path.GetTempPath(),
            "Screenshot.App.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDirectory);
    }

    [Fact]
    public void SavesACapturedImageAsPng()
    {
        var virtualDesktop = VirtualScreen.GetBounds();
        var region = new ScreenRegion(virtualDesktop.X, virtualDesktop.Y, 1, 1);

        using var image = ScreenCaptureService.Capture(region);
        var savedPath = CaptureFileService.SaveAsPng(image, _testDirectory);
        var header = new byte[8];

        using (var stream = File.OpenRead(savedPath))
        {
            _ = stream.Read(header, 0, header.Length);
        }

        Assert.True(File.Exists(savedPath));
        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, header);
    }

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(_testDirectory))
        {
            File.Delete(file);
        }

        Directory.Delete(_testDirectory);
    }
}
