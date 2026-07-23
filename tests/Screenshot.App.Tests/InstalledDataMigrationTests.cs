using System.IO;
using Screenshot.App.Infrastructure;

namespace Screenshot.App.Tests;

public sealed class InstalledDataMigrationTests : IDisposable
{
    private readonly string _testDirectory = Path.Combine(
        Path.GetTempPath(),
        $"ScreenshotMigrationTests-{Guid.NewGuid():N}");

    [Fact]
    public void MovesLegacyDataIntoTheApplicationDataDirectory()
    {
        var source = Path.Combine(_testDirectory, "legacy");
        var destination = Path.Combine(_testDirectory, "installed", "ScreenshotData");
        Directory.CreateDirectory(Path.Combine(source, "Captures"));
        File.WriteAllText(Path.Combine(source, "settings.json"), "settings");
        File.WriteAllText(Path.Combine(source, "Captures", "capture.png"), "capture");

        var result = InstalledDataMigration.TryMigrateDirectory(source, destination);

        Assert.True(result.Migrated);
        Assert.Null(result.Warning);
        Assert.False(Directory.Exists(source));
        Assert.Equal("settings", File.ReadAllText(Path.Combine(destination, "settings.json")));
        Assert.Equal(
            "capture",
            File.ReadAllText(Path.Combine(destination, "Captures", "capture.png")));
    }

    [Fact]
    public void DoesNotOverwriteDifferentDataInTheDestination()
    {
        var source = Path.Combine(_testDirectory, "legacy");
        var destination = Path.Combine(_testDirectory, "installed", "ScreenshotData");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(destination);
        File.WriteAllText(Path.Combine(source, "settings.json"), "legacy");
        File.WriteAllText(Path.Combine(destination, "settings.json"), "current");

        var result = InstalledDataMigration.TryMigrateDirectory(source, destination);

        Assert.False(result.Migrated);
        Assert.NotNull(result.Warning);
        Assert.True(Directory.Exists(source));
        Assert.Equal("current", File.ReadAllText(Path.Combine(destination, "settings.json")));
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, recursive: true);
        }
    }
}
