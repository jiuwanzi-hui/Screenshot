using System.Drawing;
using System.Drawing.Text;
using System.Globalization;
using System.Windows.Media.Imaging;
using Screenshot.App.Capture;
using Screenshot.App.Text;

namespace Screenshot.App.Tests;

public sealed class OcrServiceTests
{
    [Fact]
    public void EnumeratesAvailableWindowsOcrLanguages()
    {
        var languages = OcrService.GetAvailableLanguageTags();

        Assert.NotNull(languages);
    }

    [Fact]
    public async Task ReturnsAHelpfulResultForAnUnsupportedLanguage()
    {
        using var image = CreateTextImage("OCR TEST");

        var result = await OcrService.RecognizeAsync(
            image,
            "zz-ZZ");

        Assert.False(result.IsSuccess);
        Assert.Contains("语言包", result.ErrorMessage);
    }

    [Fact]
    public async Task RecognizesTextWhenAnEnglishLanguagePackIsAvailable()
    {
        var language = OcrService.GetAvailableLanguageTags()
            .FirstOrDefault(tag => tag.StartsWith("en", StringComparison.OrdinalIgnoreCase));

        if (language is null)
        {
            return;
        }

        using var image = CreateTextImage("OCR TEST");
        var result = await OcrService.RecognizeAsync(image, language);

        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Contains("OCR", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            result.Regions,
            region => region.Text.Contains("OCR", StringComparison.OrdinalIgnoreCase) &&
                      region.Width > 0 &&
                      region.Height > 0);
    }

    private static CapturedImage CreateTextImage(string text)
    {
        var bitmap = new Bitmap(520, 150);

        using (var graphics = Graphics.FromImage(bitmap))
        using (var font = new Font("Segoe UI", 44, FontStyle.Bold, GraphicsUnit.Pixel))
        {
            graphics.Clear(Color.White);
            graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            graphics.DrawString(
                text,
                font,
                Brushes.Black,
                new PointF(16, 42),
                StringFormat.GenericTypographic);
        }

        return new CapturedImage(bitmap);
    }
}
