using System.IO;
using Screenshot.App.Core;

namespace Screenshot.App.Tests;

public sealed class SettingsValidationTests
{
    [Theory]
    [InlineData(AppTheme.System, AppTheme.AuroraMist)]
    [InlineData(AppTheme.Light, AppTheme.AuroraMist)]
    [InlineData(AppTheme.Dark, AppTheme.ForestNight)]
    [InlineData(AppTheme.CoralSky, AppTheme.CoralSky)]
    [InlineData((AppTheme)999, AppTheme.AuroraMist)]
    public void MigratesLegacyAndInvalidThemes(
        AppTheme input,
        AppTheme expected)
    {
        var normalized = (AppSettings.CreateDefault() with
        {
            Theme = input,
        }).Normalize();

        Assert.Equal(expected, normalized.Theme);
    }

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

    [Fact]
    public void InvalidOfflineTranslationQualityUsesHighQualityDefault()
    {
        var normalized = (AppSettings.CreateDefault() with
        {
            OfflineTranslationQuality = (OfflineTranslationQuality)999,
        }).Normalize();

        Assert.Equal(
            OfflineTranslationQuality.High,
            normalized.OfflineTranslationQuality);
    }

    [Fact]
    public void InvalidOptionalModelSelectionsUseBundledDefaults()
    {
        var normalized = (AppSettings.CreateDefault() with
        {
            OcrEngine = (OcrEngineMode)999,
            OfflineTranslationEngine = (OfflineTranslationEngine)999,
        }).Normalize();

        Assert.Equal(OcrEngineMode.Windows, normalized.OcrEngine);
        Assert.Equal(
            OfflineTranslationEngine.Mozilla,
            normalized.OfflineTranslationEngine);
    }
}
