using System.IO;
using Screenshot.App.Core;

namespace Screenshot.App.Tests;

public sealed class SettingsValidationTests
{
    [Fact]
    public void NormalizesScreenshotAndVideoSaveDirectories()
    {
        var settings = AppSettings.CreateDefault() with
        {
            SaveDirectory = Path.Combine("captures", "today"),
            VideoSaveDirectory = Path.Combine("videos", "today"),
        };

        var normalized = SettingsValidation.ValidateAndNormalize(settings);

        Assert.Equal(
            Path.GetFullPath(settings.SaveDirectory),
            normalized.SaveDirectory);
        Assert.Equal(
            Path.GetFullPath(settings.VideoSaveDirectory),
            normalized.VideoSaveDirectory);
    }

    [Fact]
    public void EmptyVideoSaveDirectoryUsesApplicationDefault()
    {
        var normalized = SettingsValidation.ValidateAndNormalize(
            AppSettings.CreateDefault() with { VideoSaveDirectory = " " });

        Assert.Equal(
            Path.GetFullPath(AppMetadata.DefaultVideoDirectory),
            normalized.VideoSaveDirectory);
    }
}
