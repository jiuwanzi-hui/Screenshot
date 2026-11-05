using Screenshot.App.Text;

namespace Screenshot.App.Tests;

public sealed class TranslationTechnicalTokensTests
{
    [Theory]
    [InlineData("Version 7.12.0-beta.2 uses runtime.dll.")]
    [InlineData(@"Read %AppData%\Tool\settings.json before uninstalling.")]
    [InlineData(@"Run C:\Apps\Tool.exe, then restart.")]
    [InlineData("Run \"C:\\Program Files\\Tool\\App.exe\" before continuing.")]
    [InlineData(@"Read \\server\share\settings.ini.")]
    [InlineData("Open model.onnx and model.gguf with version v10.20.30+build.4.")]
    public void TechnicalFieldsRoundTripExactlyAndCannotDisappear(string source)
    {
        var fields = new TranslationTechnicalTokens(source);
        Assert.NotEqual(source, fields.Text);
        Assert.True(fields.TryRestore(fields.Text, out var restored));
        Assert.Equal(source, restored);
        Assert.False(fields.TryRestore("这是一段遗漏技术字段的译文。", out _));
    }

    [Fact]
    public void RepeatedFieldsMustBePreservedAndNoUnknownTokenMayEscape()
    {
        var fields = new TranslationTechnicalTokens("Compare 1.2.3 with 1.2.3, then 2.3.4.");
        Assert.False(fields.TryRestore("比较 ZXQKEEPAQXZ 与 ZXQKEEPBQXZ。", out _));
        Assert.False(fields.TryRestore(fields.Text + " ZXQKEEPUNKNOWNQXZ", out _));
        Assert.True(fields.TryRestore("版本 ZXQKEEPBQXZ 与 zxqkeepaqxz、ZXQKEEPAQXZ 对比。", out var restored));
        Assert.Equal("版本 2.3.4 与 1.2.3、1.2.3 对比。", restored);
    }

    [Fact]
    public void RequestMapsDoNotCollideWithUserTextOrRealtimeGlossary()
    {
        var fields = new TranslationTechnicalTokens("Keep ZXQKEEPAQXZ and ZXQTERMAQXZ with 1.2.3.");
        Assert.True(fields.TryRestore(fields.Text, out var restored));
        Assert.Equal("Keep ZXQKEEPAQXZ and ZXQTERMAQXZ with 1.2.3.", restored);
        var other = new TranslationTechnicalTokens("Version 9.8.7.");
        Assert.True(other.TryRestore(other.Text, out var second));
        Assert.Equal("Version 9.8.7.", second);
    }

    [Theory]
    [InlineData("Please save the document.")]
    [InlineData("There are 83 million questions and 1.5 million participants.")]
    [InlineData("Archdruids Lunarwing Form")]
    [InlineData("UserSettings.json")]
    [InlineData("TranslationModels.dll")]
    [InlineData("(user_settings.json)")]
    public void OrdinaryProseAndQuantitiesKeepExistingPrompt(string source)
    {
        var fields = new TranslationTechnicalTokens(source);
        Assert.Equal(source, fields.Text);
        Assert.Empty(fields.Instructions);
    }

    [Fact]
    public void RestoringFieldsDoesNotBypassNumberCompletenessChecks()
    {
        const string source = "Version 1.2.3 requires 4 packages.";
        var fields = new TranslationTechnicalTokens(source);
        Assert.True(fields.TryRestore("版本 ZXQKEEPAQXZ 需要 5 个包。", out var restored));
        Assert.Contains("数字", TranslationCompletenessValidator.Validate(source, restored, "zh"));
    }
}
