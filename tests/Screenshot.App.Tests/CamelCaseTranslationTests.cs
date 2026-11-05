using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net;
using System.Net.Http;
using Screenshot.App.Capture;
using Screenshot.App.Text;
using Xunit.Abstractions;

namespace Screenshot.App.Tests;

public sealed class CamelCaseTranslationTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("TranslationModels", "Translation Models")]
    [InlineData("translationModels", "translation Models")]
    [InlineData("getUserName", "get User Name")]
    [InlineData("HTTPServer", "HTTP Server")]
    [InlineData("XMLHttpRequest", "XML Http Request")]
    [InlineData("UserName2", "User Name2")]
    [InlineData("getId", "get Id")]
    [InlineData("TranslationProvider", "Translation Provider")]
    [InlineData("ConnectionTimeout", "Connection Timeout")]
    [InlineData("enableAutoSave", "enable Auto Save")]
    [InlineData("user_settings", "user settings")]
    [InlineData("connection-timeout", "connection timeout")]
    [InlineData("HTTP_SERVER", "HTTP SERVER")]
    [InlineData("(TranslationProvider)", "(Translation Provider)")]
    [InlineData("[ConnectionTimeout]", "[Connection Timeout]")]
    [InlineData("“UserSettings”", "“User Settings”")]
    [InlineData("`getUserName`", "`get User Name`")]
    [InlineData("ConnectionTimeout:", "Connection Timeout:")]
    [InlineData("TranslationProvider.", "Translation Provider.")]
    [InlineData("TranslationProvider.cs", "Translation Provider.cs")]
    [InlineData("UserSettings.json", "User Settings.json")]
    [InlineData("TranslationModels.dll", "Translation Models.dll")]
    [InlineData("(user_settings.json)", "(user settings.json)")]
    public void SelectedCompoundLabelsReachTranslationAndCannotBeAcceptedAsUntranslated(string text, string expanded)
    {
        Assert.False(TranslationTargetLanguageMatcher.IsLikelyInvariant(text));
        Assert.False(TranslationTargetLanguageMatcher.IsAlreadyTargetLanguage(text, "zh-Hans"));
        Assert.True(TranslationTargetLanguageMatcher.ShouldTranslate(text, "auto", "zh-Hans"));
        Assert.True(TranslationTargetLanguageMatcher.ShouldTranslate(text, "en", "zh-Hant"));
        Assert.Equal(expanded, TranslationTextNormalizer.Normalize(text));
        Assert.Equal(expanded, TranslationTextNormalizer.Normalize(expanded));
        Assert.NotNull(TranslationCompletenessValidator.Validate(text, text, "zh-Hans"));
        Assert.NotNull(TranslationCompletenessValidator.Validate(text, expanded, "zh-Hans"));
        Assert.False(OrderedTranslationProvider.HasMeaningfulTranslation(
            ["Please save the document.", text], ["请保存文档。", text], "zh-Hans"));
    }

    [Theory]
    [InlineData(@"D:\Program Files\SnapCut\TranslationModels")]
    [InlineData("https://example.com/TranslationModels")]
    [InlineData("TranslationModels/model.bin")]
    [InlineData("https://example.com/TranslationModels.dll")]
    [InlineData("../UserSettings.json")]
    [InlineData("TranslationModels/model_settings.bin")]
    [InlineData("plain.dll")]
    [InlineData("@TranslationModels")]
    [InlineData("CPU")]
    [InlineData("HTTP")]
    [InlineData("30MB")]
    public void PathsHandlesAcronymsAndMeasurementsKeepTheirExistingProtection(string text)
    {
        Assert.True(TranslationTargetLanguageMatcher.IsLikelyInvariant(text));
        Assert.False(TranslationTargetLanguageMatcher.ShouldTranslate(text, "auto", "zh-Hans"));
        Assert.Equal(text, TranslationTextNormalizer.Normalize(text));
    }

    [Theory]
    [InlineData("var TranslationModels = models;")]
    [InlineData("Read TranslationModels before starting.")]
    [InlineData("AcmeWidget supports offline translation.")]
    [InlineData("版本 Version 2")]
    public void OnlyStandaloneLabelsHaveWordBoundariesExpanded(string text) =>
        Assert.Equal(text, TranslationTextNormalizer.Normalize(text));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OnlineRequestsExpandTheSelectedLabelAndPreserveSkippedSegmentPositions(bool batch)
    {
        var handler = new ReplyHandler(batch
            ? """{"choices":[{"message":{"content":"{\"translations\":[\"翻译模型\"]}"}}]}"""
            : """{"choices":[{"message":{"content":"翻译模型"}}]}""");
        using var client = new HttpClient(handler);
        var provider = new OpenAiCompatibleTranslationProvider("https://translation.example/v1/chat/completions", "test-key", client);
        if (batch)
        {
            var result = await new OrderedTranslationProvider([provider]).TranslateSegmentsAsync(
                ["常规设置", "TranslationModels", "123"], "auto", "zh-Hans");
            Assert.True(result.IsSuccess, result.ErrorMessage);
            Assert.Empty(result.FailedSegmentIndices ?? []);
            Assert.Equal(["常规设置", "翻译模型", "123"], result.Segments);
        }
        else
        {
            var result = await provider.TranslateAsync("TranslationModels", "auto", "zh-Hans");
            Assert.True(result.IsSuccess, result.ErrorMessage);
            Assert.Equal("翻译模型", result.Text);
        }
        Assert.Contains("Translation Models", Assert.Single(handler.Requests));
    }

    [Fact]
    public async Task EchoedCompoundLabelFailsInsteadOfClaimingItIsAlreadyChinese()
    {
        using var client = new HttpClient(new ReplyHandler("""{"choices":[{"message":{"content":"TranslationModels"}}]}"""));
        var result = await new OpenAiCompatibleTranslationProvider(
            "https://translation.example/v1/chat/completions", "test-key", client)
            .TranslateAsync("TranslationModels", "auto", "zh-Hans");
        Assert.False(result.IsSuccess);
        Assert.Contains("译文质量不达标", result.ErrorMessage);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task PublicRequestsExpandLabelsAndFailuresKeepTheirOriginalText(bool youdao, bool echo)
    {
        var translation = echo ? "Translation Models" : "翻译模型";
        var handler = new ReplyHandler(youdao
            ? System.Text.Json.JsonSerializer.Serialize(new { errorCode = "0", translateResults = new[] { new { query = "Translation Models", translation } } })
            : System.Text.Json.JsonSerializer.Serialize(new { trans_result = new[] { new { src = "Translation Models", dst = translation } } }));
        using var client = new HttpClient(handler);
        ITranslationProvider provider = youdao ? new YoudaoTranslationProvider("test", "key", client)
            : new BaiduTranslationProvider(Guid.NewGuid().ToString(), "key", client);
        var result = await provider.TranslateSegmentsAsync(["常规设置", "TranslationModels", "123"], "auto", "zh-Hans");
        var fields = Assert.Single(handler.Requests).Split('&').Select(pair => pair.Split('=', 2))
            .ToLookup(pair => WebUtility.UrlDecode(pair[0]), pair => WebUtility.UrlDecode(pair[1]));
        Assert.Equal("Translation Models", Assert.Single(fields["q"]));
        Assert.Equal(["常规设置", echo ? "TranslationModels" : "翻译模型", "123"], result.Segments);
        if (echo) Assert.Equal(1, Assert.Single(result.FailedSegmentIndices!));
        else Assert.Empty(result.FailedSegmentIndices!);
    }

    [TranslationModelFact]
    public async Task DifferentLabelFormsReachTheInstalledModel()
    {
        var modelDirectory = Environment.GetEnvironmentVariable("SNAPCUT_TRANSLATION_MODEL_DIRECTORY")!;
        using var manager = new LocalLargeTranslationModelManager(modelDirectory,
            lightweight: Path.GetFileName(modelDirectory).Equals("Hy-MT2-1.8B", StringComparison.OrdinalIgnoreCase));
        var provider = new OrderedTranslationProvider([new LocalLargeModelTranslationProvider(manager)]);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        (string Text, string Meaning)[] samples =
        [
            ("ConnectionTimeout", "超时"), ("getUserName", "用户"),
            ("HTTPServer", "服务"), ("XMLHttpRequest", "请求"),
            ("user_settings", "设置"), ("connection-timeout", "超时"),
            ("(TranslationProvider)", "翻译"), ("ConnectionTimeout:", "超时"),
            ("TranslationProvider.cs", "翻译"), ("UserSettings.json", "设置"),
        ];
        var failures = new List<string>();
        foreach (var (text, meaning) in samples)
        {
            var result = await provider.TranslateSegmentsAsync([text], "auto", "zh-Hans", cancellation.Token);
            output.WriteLine($"{manager.DisplayName}: {text} => {string.Join(" | ", result.Segments)}; {result.ErrorMessage}");
            if (!result.IsSuccess || result.FailedSegmentIndices is { Count: > 0 } ||
                result.Segments.Count != 1 || !result.Segments[0].Contains(meaning, StringComparison.Ordinal))
                failures.Add(text + ": " + result.ErrorMessage);
        }
        Assert.Empty(failures);
    }

    [TranslationModelFact]
    public async Task ProviderSelectionReachesRealOcrTranslationAndOverlayLayout()
    {
        using var stream = typeof(CamelCaseTranslationTests).Assembly.GetManifestResourceStream(
            "Screenshot.App.Tests.Fixtures.translation-provider-selection.png")!;
        using var screenshot = new Bitmap(stream);
        using var ocr = new HighQualityOcrModelManager(Environment.GetEnvironmentVariable("SNAPCUT_OCR_MODEL_DIRECTORY"));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var modelDirectory = Environment.GetEnvironmentVariable("SNAPCUT_TRANSLATION_MODEL_DIRECTORY")!;
        using var manager = new LocalLargeTranslationModelManager(modelDirectory,
            lightweight: Path.GetFileName(modelDirectory).Equals("Hy-MT2-1.8B", StringComparison.OrdinalIgnoreCase));
        var provider = new OrderedTranslationProvider([new LocalLargeModelTranslationProvider(manager)]);
        foreach (var bounds in new[] { new Rectangle(496, 42, 154, 50), new Rectangle(499, 46, 148, 42) })
        {
            using var image = new CapturedImage(screenshot.Clone(bounds, PixelFormat.Format32bppArgb));
            var recognition = await HighQualityOcrService.RecognizeAsync(image, ocr, cancellation.Token);
            output.WriteLine($"{bounds}: {recognition.Text}; success: {recognition.IsSuccess}; {recognition.ErrorMessage}");
            Assert.True(recognition.IsSuccess, recognition.ErrorMessage);
            Assert.Equal("TranslationProvider", recognition.Text.Trim());
            // Use the same geometry/grouping and translated-text normalization
            // as the screenshot toolbar, without showing a desktop overlay.
            var regions = TranslationPresentationLayout.GroupParagraphs(TranslationInkBounds.Refine(image.Bitmap,
                TranslationPresentationLayout.TightenToWordBounds(recognition.Regions, recognition.Words)));
            Assert.NotEmpty(regions);
            var inputs = regions.Select(region => region.Text).ToArray();
            Assert.All(inputs, text => Assert.False(TranslationTargetLanguageMatcher.IsAlreadyTargetLanguage(text, "zh-Hans")));
            var watch = Stopwatch.StartNew();
            var result = await provider.TranslateSegmentsAsync(inputs, "auto", "zh-Hans", cancellation.Token);
            output.WriteLine($"{manager.DisplayName}: {watch.ElapsedMilliseconds} ms; {string.Join(" | ", result.Segments)}; {result.ErrorMessage}; {result.Notice}");
            Assert.True(result.IsSuccess, result.ErrorMessage);
            Assert.Empty(result.FailedSegmentIndices ?? []);
            Assert.Contains("翻译", Assert.Single(result.Segments));
            Assert.Contains("提供", result.Segments[0]);
            Assert.True(TranslationPresentationLayout.HasMeaningfulTranslation(inputs[0],
                TranslationPresentationLayout.NormalizeTranslatedText(inputs[0], result.Segments[0])));
        }
    }

    [TranslationModelFact]
    public async Task SuppliedSelectionReachesRealOcrAndInstalledTranslationModel()
    {
        var modelDirectory = Environment.GetEnvironmentVariable("SNAPCUT_TRANSLATION_MODEL_DIRECTORY")!;
        var lightweight = Path.GetFileName(modelDirectory).Equals("Hy-MT2-1.8B", StringComparison.OrdinalIgnoreCase);
        using var stream = typeof(CamelCaseTranslationTests).Assembly.GetManifestResourceStream(
            "Screenshot.App.Tests.Fixtures.camel-case-selection.png")!;
        using var screenshot = new Bitmap(stream);
        // Sample only the selected text inside the supplied screenshot's frame.
        using var image = new CapturedImage(screenshot.Clone(new Rectangle(438, 140, 133, 26), PixelFormat.Format32bppArgb));
        using var ocr = new HighQualityOcrModelManager(Environment.GetEnvironmentVariable("SNAPCUT_OCR_MODEL_DIRECTORY"));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var watch = Stopwatch.StartNew();
        var recognition = await HighQualityOcrService.RecognizeAsync(image, ocr, cancellation.Token);
        output.WriteLine($"OCR: {watch.ElapsedMilliseconds} ms; {recognition.Text}");
        Assert.True(recognition.IsSuccess, recognition.ErrorMessage);
        Assert.Equal("TranslationModels", recognition.Text.Replace(" ", "", StringComparison.Ordinal).Trim());
        var text = recognition.Text.Trim();
        Assert.True(TranslationTargetLanguageMatcher.ShouldTranslate(text, "auto", "zh-Hans"));
        using var manager = new LocalLargeTranslationModelManager(modelDirectory, lightweight: lightweight);
        var provider = new OrderedTranslationProvider([new LocalLargeModelTranslationProvider(manager)]);
        for (var iteration = 0; iteration < 2; iteration++)
        {
            watch.Restart();
            var result = await provider.TranslateSegmentsAsync([text], "auto", "zh-Hans", cancellation.Token);
            output.WriteLine($"{manager.DisplayName}, pass {iteration + 1}: {watch.ElapsedMilliseconds} ms; {string.Join(" | ", result.Segments)}; {result.ErrorMessage}; {result.Notice}");
            Assert.True(result.IsSuccess, result.ErrorMessage);
            Assert.Empty(result.FailedSegmentIndices ?? []);
            Assert.Contains("翻译", Assert.Single(result.Segments));
            Assert.Contains("模型", result.Segments[0]);
        }
    }

    private sealed class ReplyHandler(string json) : HttpMessageHandler
    {
        internal List<string> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) };
        }
    }
}
