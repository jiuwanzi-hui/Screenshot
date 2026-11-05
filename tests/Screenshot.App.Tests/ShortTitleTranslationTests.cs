using Screenshot.App.Text;
using Screenshot.App.Capture;
using System.Drawing;
using System.Diagnostics;
using System.IO;
using Xunit.Abstractions;

namespace Screenshot.App.Tests;

public sealed class ShortTitleTranslationTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UncertainTitleReachesModelAvailabilityCheck(bool lightweight)
    {
        using var manager = new LocalLargeTranslationModelManager(
            Path.Combine(Path.GetTempPath(), "snapcut-absent-model-" + Guid.NewGuid().ToString("N")), lightweight: lightweight);
        var provider = new LocalLargeModelTranslationProvider(manager);
        var result = await provider.TranslateAsync("Archdruid's Lunarwing Form", "auto", "zh-Hans");
        Assert.False(result.IsSuccess);
        Assert.Contains("需要下载", result.ErrorMessage);
        Assert.DoesNotContain("手动选择源语言", result.ErrorMessage);
    }

    [Fact]
    public void AutoPromptDoesNotInventEnglishAndExplicitSourceKeepsTrainedTemplate()
    {
        var automatic = LocalLargeModelTranslationProvider.CreatePrompt("Lunarwing Form", "auto", "zh");
        Assert.Contains("Identify the source language", automatic);
        Assert.Contains("Chinese", automatic);
        Assert.DoesNotContain("English", automatic);
        var explicitFrench = LocalLargeModelTranslationProvider.CreatePrompt("Forme lunaire", "fr", "zh");
        Assert.Contains("French (fr)", explicitFrench);
        Assert.DoesNotContain("Identify the source language", explicitFrench);
    }

    [Fact]
    public void ChineseTranslationContainingUnrequestedKoreanFailsQualityValidation()
    {
        Assert.Contains("韩语", TranslationCompletenessValidator.Validate("Archdruid's Lunarwing Form", "阿奇德鲁伊드의 月影形态", "zh-Hans"));
    }

    [LocalRuntimeTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SuppliedTitleIsRecognizedThenTranslatedBySelectedOfflineModel(bool lightweight)
    {
        var modelRoot = Environment.GetEnvironmentVariable("SNAPCUT_TRANSLATION_MODEL_ROOT")!;
        using var manager = new LocalLargeTranslationModelManager(Path.Combine(modelRoot,
            lightweight ? "Hy-MT2-1.8B" : "TranslateGemma-4B-Q4-K-M"), lightweight: lightweight);
        using var ocrManager = new HighQualityOcrModelManager(Path.Combine(modelRoot, Screenshot.App.Core.AppMetadata.HighQualityOcrModelDirectoryName));
        using var stream = typeof(ShortTitleTranslationTests).Assembly.GetManifestResourceStream(
            "Screenshot.App.Tests.Fixtures.small-yellow-title.png")!;
        using var image = new CapturedImage(new Bitmap(stream));
        var recognition = await HighQualityOcrService.RecognizeAsync(image, ocrManager);
        Assert.Equal("Archdruid's Lunarwing Form", recognition.Text);
        var detector = Cld3OfflineLanguageDetector.Shared.Detect(recognition.Text);
        output.WriteLine($"detector={detector}");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var watch = Stopwatch.StartNew();
        var result = await new LocalLargeModelTranslationProvider(manager).TranslateAsync(
            recognition.Text, "auto", "zh-Hans", timeout.Token);
        output.WriteLine($"{manager.DisplayName}: {watch.ElapsedMilliseconds} ms, {result}");
        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Null(TranslationCompletenessValidator.Validate(recognition.Text, result.Text, "zh"));
        // This test covers the previously blocked OCR-to-translation path.
        // These models do not consistently know the game's official glossary:
        // preserve the generated output above for separate semantic review.
        Assert.Contains("月", result.Text);
        Assert.Contains("形态", result.Text);
        Assert.Contains("源语言由所选离线模型自动判断", result.Notice);
    }
}
