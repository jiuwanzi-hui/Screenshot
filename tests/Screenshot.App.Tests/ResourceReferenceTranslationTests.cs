using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using Screenshot.App.Capture;
using Screenshot.App.Text;
using Xunit.Abstractions;

namespace Screenshot.App.Tests;

public sealed class ResourceReferenceTranslationTests(ITestOutputHelper output)
{
    internal const string InstallerMessage = @"The installer has insufficient privileges to access this directory: C:\Program Files\Paradox Interactive. The installation cannot continue. Log on as an administrator or contact your system administrator.";

    [Theory]
    [InlineData(InstallerMessage)]
    [InlineData("The installer has insufficient privileges to access this\ndirectory: C:\\Program Files\\Paradox Interactive. The\ninstallation cannot continue. Log on as an administrator or\ncontact your system administrator.")]
    [InlineData(@"Open C:\Temp")]
    [InlineData(@"directory: C:\Program Files\Paradox Interactive. The")]
    [InlineData(@"C:\Temp is unavailable. Please select another directory.")]
    [InlineData(@"C:\Temp\readme.txt could not be opened.")]
    [InlineData(@"\\server\share is unavailable.")]
    [InlineData("Status: installation failed")]
    [InlineData("https://example.com is unavailable. Please try again.")]
    [InlineData("Read https://example.com/help")]
    [InlineData("README.md contains the installation instructions.")]
    [InlineData("docs/install.md contains instructions.")]
    [InlineData("Email support@example.com")]
    [InlineData("Contact <support@example.com>")]
    [InlineData("Preferences >")]
    [InlineData(@"Impossible d'ouvrir C:\Temp.")]
    public void ResourceInsideProseDoesNotSkipTranslation(string text)
    {
        Assert.False(TranslationTargetLanguageMatcher.IsLikelyInvariant(text));
        Assert.False(TranslationTargetLanguageMatcher.IsAlreadyTargetLanguage(text, "zh-Hans"));
        Assert.True(TranslationTargetLanguageMatcher.ShouldTranslate(text, "auto", "zh-Hans"));
        Assert.True(TranslationTargetLanguageMatcher.ShouldTranslate(text, "en", "zh-Hans"));
    }

    [Theory]
    [InlineData(@"C:\Program Files\Paradox Interactive")]
    [InlineData(@"D:\Program Files\SnapCut\TranslationModels")]
    [InlineData("\"C:\\Program Files\\Paradox Interactive\"")]
    [InlineData(@"\\server\share\My Documents\report.txt")]
    [InlineData(@".\model\weights.bin")]
    [InlineData("../UserSettings.json")]
    [InlineData("TranslationModels/model.bin")]
    [InlineData("src/Screenshot.App/Capture/AutomaticImageOverlapMatcher.cs +2240")]
    [InlineData("https://example.com/help")]
    [InlineData("mailto:support@example.com")]
    [InlineData("support@example.com")]
    [InlineData("@SupportTeam")]
    [InlineData("<input type=\"text\">")]
    public void StandaloneReferencesKeepTheirProtection(string text)
    {
        Assert.True(TranslationTargetLanguageMatcher.IsLikelyInvariant(text));
        Assert.False(TranslationTargetLanguageMatcher.ShouldTranslate(text, "auto", "zh-Hans"));
    }

    [Theory]
    [InlineData("auto")]
    [InlineData("en")]
    public async Task ParagraphContainingPathReachesProviderAndRetainsSegmentPositions(string source)
    {
        Assert.NotNull(TranslationCompletenessValidator.Validate(InstallerMessage, InstallerMessage, "zh-Hans"));
        var provider = new RecordingProvider();
        string[] input = [@"C:\Program Files\Paradox Interactive", InstallerMessage, "1274"];
        var result = await new OrderedTranslationProvider([provider]).TranslateSegmentsAsync(input, source, "zh-Hans");
        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Equal([InstallerMessage], Assert.Single(provider.Requests));
        Assert.Equal([input[0], RecordingProvider.Translation, input[2]], result.Segments);
        Assert.Empty(result.FailedSegmentIndices ?? []);
    }

    private sealed class RecordingProvider : ITranslationProvider
    {
        internal const string Translation = @"安装程序没有足够的权限访问此目录：C:\Program Files\Paradox Interactive。安装无法继续。请以管理员身份登录或联系系统管理员。";
        public string Id => "resource-reference-test";
        internal List<string[]> Requests { get; } = [];
        public Task<TranslationResult> TranslateAsync(string text, string sourceLanguage, string targetLanguage,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException("Use segments.");
        public Task<TranslationSegmentsResult> TranslateSegmentsAsync(IReadOnlyList<string> segments, string sourceLanguage,
            string targetLanguage, CancellationToken cancellationToken = default)
        {
            Requests.Add(segments.ToArray());
            return Task.FromResult(new TranslationSegmentsResult(true, segments.Select(_ => Translation).ToArray(), null));
        }
    }

    [TranslationModelFact]
    public async Task InstallerScreenshotReachesOcrOfflineModelAndTranslatedOverlay()
    {
        using var stream = typeof(ResourceReferenceTranslationTests).Assembly.GetManifestResourceStream(
            "Screenshot.App.Tests.Fixtures.installer-path-selection.png")!;
        using var screenshot = new Bitmap(stream);
        using var image = new CapturedImage(screenshot.Clone(new Rectangle(214, 40, 534, 163), PixelFormat.Format32bppArgb));
        using var ocr = new HighQualityOcrModelManager(Environment.GetEnvironmentVariable("SNAPCUT_OCR_MODEL_DIRECTORY"));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var watch = Stopwatch.StartNew();
        var recognition = await HighQualityOcrService.RecognizeAsync(image, ocr, cancellation.Token);
        output.WriteLine($"OCR: {watch.ElapsedMilliseconds} ms; {recognition.Text}; {recognition.ErrorMessage}");
        Assert.True(recognition.IsSuccess, recognition.ErrorMessage);
        Assert.Contains("insufficient privileges", recognition.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Paradox", recognition.Text, StringComparison.OrdinalIgnoreCase);
        var regions = TranslationPresentationLayout.GroupParagraphs(TranslationInkBounds.Refine(image.Bitmap,
            TranslationPresentationLayout.TightenToWordBounds(recognition.Regions, recognition.Words)));
        var input = regions.Select(region => region.Text).ToArray();
        Assert.NotEmpty(input);
        Assert.False(input.All(text => TranslationTargetLanguageMatcher.IsAlreadyTargetLanguage(text, "zh-Hans")));
        using var model = new LocalLargeTranslationModelManager(Environment.GetEnvironmentVariable("SNAPCUT_TRANSLATION_MODEL_DIRECTORY"));
        var translator = new OrderedTranslationProvider([new LocalLargeModelTranslationProvider(model)]);
        for (var pass = 0; pass < 2; pass++)
        {
            watch.Restart();
            var result = await translator.TranslateSegmentsAsync(input, "auto", "zh-Hans", cancellation.Token);
            var text = string.Join(" ", result.Segments);
            output.WriteLine($"{model.DisplayName}, pass {pass + 1}: {watch.ElapsedMilliseconds} ms; {text}; {result.ErrorMessage}; {result.Notice}");
            Assert.True(result.IsSuccess, result.ErrorMessage);
            Assert.Empty(result.FailedSegmentIndices ?? []);
            Assert.Contains("权限", text);
            Assert.Contains("安装", text);
            Assert.Contains("管理员", text);
            Assert.Contains("Paradox", text);
            Assert.True(TranslationPresentationLayout.HasMeaningfulTranslation(string.Join(" ", input),
                TranslationPresentationLayout.NormalizeTranslatedText(string.Join(" ", input), text)));
        }
    }
}
