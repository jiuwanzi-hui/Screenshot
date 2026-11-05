using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Screenshot.App.Text;

namespace Screenshot.App.Tests;

public sealed class MultilingualTranslationTests
{
    [TranslationModelFact]
    public async Task ChinesePageTranslatesIntoEightLanguagesWithoutLosingNumbers()
    {
        using var manager = new LocalLargeTranslationModelManager(Environment.GetEnvironmentVariable("SNAPCUT_TRANSLATION_MODEL_DIRECTORY"));
        var provider = new LocalLargeModelTranslationProvider(manager);
        string[] input = [
            "会议将于2026年9月12日召开，共有25位参与者。请提前保存文档并检查网络连接。",
            "过去18年，我们累计解答了8300万个问题。每隔21秒，就有用户提出新的问题。",
            "用户可以通过快捷键截取屏幕，识别图片中的文字，然后选择目标语言进行翻译。所有配置会自动保存。",
            "软件支持在线和离线翻译。请按照自己的偏好调整顺序，系统会保留已经完成的段落，只重试失败的部分。",
            "请在下午3点之前提交报告。如果遇到问题，请先检查设置，再重新尝试。谢谢您的配合。",
        ];
        var reports = new List<object>();
        var failures = new List<string>();
        foreach (var target in new[] { "en", "ja", "ko", "fr", "de", "es", "ru", "ar" })
        {
            var watch = Stopwatch.StartNew();
            var result = await provider.TranslateSegmentsAsync(input, "zh", target);
            var diagnostic = new List<object>();
            foreach (var index in result.FailedSegmentIndices ?? [])
            {
                var raw = await manager.InferenceSession.RunAsync((_, token) => manager.InferenceSession.CompleteAsync(
                    LocalLargeModelTranslationProvider.CreatePrompt(input[index], "zh", target), 1536, token), CancellationToken.None);
                diagnostic.Add(new { index, raw });
                failures.Add($"{target}:{index}");
            }
            reports.Add(new { diagnostic, source = "zh", target, seconds = watch.Elapsed.TotalSeconds,
                result.IsSuccess, result.ErrorMessage, result.FailedSegmentIndices, result.Segments });
            if (Environment.GetEnvironmentVariable("SNAPCUT_TRANSLATION_PAGE_REPORT") is { Length: > 0 } path)
                await System.IO.File.WriteAllTextAsync(path, System.Text.Json.JsonSerializer.Serialize(reports));
            Assert.True(result.IsSuccess, result.ErrorMessage);

            Assert.Equal(input.Length, result.Segments.Count);
        }
        Assert.Empty(failures);
    }

    private static readonly JsonSerializerOptions ReportOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
    [Theory]
    [InlineData("La délégation compte 1274 personnes.")]
    [InlineData("La delegación tiene 1274 miembros.")]
    [InlineData("Nous devons traduire tous les paragraphes.")]
    [InlineData("El programa permite tomar capturas de pantalla.")]
    public void LatinScriptDoesNotMakeFrenchOrSpanishEnglish(string source) =>
        Assert.False(TranslationTargetLanguageMatcher.IsAlreadyTargetLanguage(source, "en"));

    [Fact]
    public void ExplicitJapaneseSourceKeepsHanOnlyHeadingsInTranslation() =>
        Assert.True(TranslationTargetLanguageMatcher.ShouldTranslate("中国選手団名簿発表", "ja", "zh-Hans"));

    [Theory]
    [InlineData("548人が初出場で、306人は出場経験がある。（編集WB）", "zh-Hans")]
    [InlineData("软件可以使用 CPU 进行离线翻译。", "en")]
    [InlineData("La traduction utilise le CPU.", "en")]
    [InlineData("البرنامج يستخدم CPU للترجمة.", "en")]
    public void AnAcronymCannotSuppressTranslationOfTheSurroundingParagraph(string text, string target)
    {
        Assert.False(TranslationTargetLanguageMatcher.IsLikelyInvariant(text));
        Assert.False(TranslationTargetLanguageMatcher.IsAlreadyTargetLanguage(text, target));
    }

    [Theory]
    [InlineData("代表团有1274人，其中815名运动员。", "The delegation has 9274 members, including 815 athletes.", "en", false)]
    [InlineData("代表团有1274人，其中815名运动员。", "The delegation has 1,274 members, including 815 athletes.", "en", true)]
    [InlineData("2026年09月04日", "4 September 2026", "en", true)]
    [InlineData("The delegation has 1274 members.", "يضم الوفد ١٢٧٤ عضواً.", "ar", true)]
    public void RejectsChangedNumbersButAcceptsDateAndDigitFormatting(string source, string translated, string target, bool valid) =>
        Assert.Equal(valid, TranslationCompletenessValidator.Validate(source, translated, target) is null);

    [Fact]
    public void RejectsJapaneseParagraphLeftInsideChineseTranslation() =>
        Assert.NotNull(TranslationCompletenessValidator.Validate("出場選手のうち548人が初出場です。",
            "参赛选手中，548人が初出場です。", "zh-Hans"));

    [Fact]
    public void OcrWordBreakRepairPreservesOtherLanguagesAndParagraphs()
    {
        Assert.Equal("選手とバドミントン。\nCPU usage 1274", TranslationTextNormalizer.Normalize(
            "選 手とバドミン トン。\nCPU usage 1274"));
    }

    [Fact]
    public void LongParagraphSplittingPreservesEveryCharacter()
    {
        var source = string.Concat(Enumerable.Repeat("第20回アジア競技大会。出場選手は815人です。\n", 80)) + "末尾😀";
        var pieces = LocalLargeModelTranslationProvider.SplitText(source);
        Assert.Equal(source, string.Concat(pieces));
        Assert.All(pieces, piece => Assert.InRange(piece.Length, 1, 600));
        Assert.All(pieces, piece => Assert.False(char.IsHighSurrogate(piece[^1])));
    }

    [Fact]
    public async Task MissingModelNamesTheRequiredDownloadAndLanguagePair()
    {
        using var manager = new LocalLargeTranslationModelManager(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        var result = await new LocalLargeModelTranslationProvider(manager).TranslateAsync(
            "La délégation compte 1274 personnes.", "fr", "en");
        Assert.False(result.IsSuccess);
        Assert.Contains("TranslateGemma 4B", result.ErrorMessage);
        Assert.Contains("下载", result.ErrorMessage);
        Assert.Contains("Français → English", result.ErrorMessage);
    }

    [TranslationModelFact]
    public async Task SuppliedScreenshotCanBeRecognizedAndTranslatedCompletely()
    {
        var modelDirectory = Environment.GetEnvironmentVariable("SNAPCUT_TRANSLATION_MODEL_DIRECTORY")!;
        var screenshotPath = Environment.GetEnvironmentVariable("SNAPCUT_TRANSLATION_SCREENSHOT_PATH");
        Assert.False(string.IsNullOrWhiteSpace(screenshotPath), "Set the supplied screenshot path for this opt-in reproduction.");
        using var bitmap = new System.Drawing.Bitmap(screenshotPath!);
        // The supplied screenshot's lower half is the old result popup. Only
        // analyze the original Japanese article above that popup.
        using var image = new Screenshot.App.Capture.CapturedImage(bitmap.Clone(
            new System.Drawing.Rectangle(0, 0, bitmap.Width, 465), System.Drawing.Imaging.PixelFormat.Format32bppArgb));
        using var ocrManager = new HighQualityOcrModelManager(Path.Combine(Path.GetDirectoryName(modelDirectory)!, Screenshot.App.Core.AppMetadata.HighQualityOcrModelDirectoryName));
        var timer = Stopwatch.StartNew();
        var recognition = await HighQualityOcrService.RecognizeAsync(image, ocrManager);
        var ocrSeconds = timer.Elapsed.TotalSeconds;
        Assert.True(recognition.IsSuccess, recognition.ErrorMessage);
        var regions = TranslationPresentationLayout.GroupParagraphs(
            TranslationPresentationLayout.TightenToWordBounds(recognition.Regions, recognition.Words)
                .OrderBy(region => region.Y).ThenBy(region => region.X).ToArray());
        using var manager = new LocalLargeTranslationModelManager(modelDirectory);
        var translated = await new OrderedTranslationProvider([new LocalLargeModelTranslationProvider(manager)])
            .TranslateSegmentsAsync(regions.Select(region => region.Text).ToArray(), "auto", "zh-Hans");
        var totalSeconds = timer.Elapsed.TotalSeconds;
        var report = Environment.GetEnvironmentVariable("SNAPCUT_TRANSLATION_SCREENSHOT_REPORT")!;
        await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new
        {
            ocrSeconds, totalSeconds, source = regions.Select(region => region.Text),
            translated.IsSuccess, translated.ErrorMessage, output = translated.Segments,
        }, ReportOptions));
        Assert.True(translated.IsSuccess, translated.ErrorMessage);
        Assert.DoesNotContain(translated.Segments, text => text.Any(c => c is >= '\u3040' and <= '\u30ff'));
        var all = string.Join("\n", translated.Segments);
        string[] numbers = ["1274", "815", "404", "411", "548", "306", "369"];
        Assert.All(numbers, number => Assert.Contains(number, all));
    }

    [TranslationModelFact]
    public async Task ColdStartCancellationDoesNotLeaveAnInferenceProcess()
    {
        using var manager = new LocalLargeTranslationModelManager(Environment.GetEnvironmentVariable("SNAPCUT_TRANSLATION_MODEL_DIRECTORY"));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(350));
        var provider = new LocalLargeModelTranslationProvider(manager);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.TranslateAsync(
            "请将这段文字翻译成英文，并保留完整含义。", "zh", "en", cancellation.Token));
        foreach (var process in Process.GetProcessesByName("llama-server"))
        {
            using (process)
                Assert.False(process.MainModule?.FileName.StartsWith(manager.InstallationDirectory, StringComparison.OrdinalIgnoreCase) == true);
        }
    }

    [TranslationModelFact]
    public async Task ModelWorkerIsReusedThenReleasedAtIdleAndOnCancellation()
    {
        using var manager = new LocalLargeTranslationModelManager(Environment.GetEnvironmentVariable("SNAPCUT_TRANSLATION_MODEL_DIRECTORY"));
        var provider = new LocalLargeModelTranslationProvider(manager);
        Assert.True((await provider.TranslateAsync("出場選手は815人です。", "ja", "zh-Hans")).IsSuccess);
        using var worker = Process.GetProcessesByName("llama-server").Single(process =>
            process.MainModule?.FileName.StartsWith(manager.InstallationDirectory, StringComparison.OrdinalIgnoreCase) == true);
        Assert.True((await provider.TranslateAsync("参加者は1274人です。", "ja", "zh-Hans")).IsSuccess);
        Assert.False(worker.HasExited);
        await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(35));
        Assert.True(worker.HasExited);
        Assert.True((await provider.TranslateAsync("出場選手は815人です。", "ja", "zh-Hans")).IsSuccess);
        using var restartedWorker = Process.GetProcessesByName("llama-server").Single(process =>
            process.MainModule?.FileName.StartsWith(manager.InstallationDirectory, StringComparison.OrdinalIgnoreCase) == true);
        Assert.NotEqual(worker.Id, restartedWorker.Id);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.TranslateAsync(
            string.Concat(Enumerable.Repeat("出場選手は815人です。", 200)), "ja", "zh-Hans", cancellation.Token));
        await restartedWorker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
    }

    [TranslationModelFact]
    public async Task InstalledModelTranslatesJapaneseArticleAndAllUnitedNationsLanguageDirections()
    {
        using var manager = new LocalLargeTranslationModelManager(Environment.GetEnvironmentVariable("SNAPCUT_TRANSLATION_MODEL_DIRECTORY"));
        Assert.True(manager.GetStatus().IsInstalled, "Install the optional multilingual model before running this test.");
        var provider = new LocalLargeModelTranslationProvider(manager);
        var records = new List<object>();
        string[] article =
        [
            "2026年アジア競技大会の中国代表団の選手名簿発表",
            "人民網日本語版　2026年09月04日16:47",
            "第20回アジア競技大会に出場する中国体育代表団が9月4日、北京で結成された。代表団は計1274人からなり、そのうち選手は815人（女子選手404人、男子選手411人）。38競技の369種目に出場する。",
            "出場選手のうち、548人がアジア競技大会初出場で、306人はオリンピックまたはアジア競技大会の出場経験がある。卓球の孫穎莎選手、王曼昱選手、飛び込みの陳芋汐選手、王宗源選手を含む、水泳、アーティスティックスイミング、バドミントン、ボクシングなど13競技から計36人のオリンピック金メダリストが今回のアジア大会に臨む。（編集WB）",
        ];
        var watch = Stopwatch.StartNew();
        var articleResult = await provider.TranslateSegmentsAsync(article, "auto", "zh-Hans");
        records.Add(new { source = "ja", target = "zh", seconds = watch.Elapsed.TotalSeconds,
            articleResult.IsSuccess, articleResult.ErrorMessage, output = articleResult.Segments });
        var samples = new Dictionary<string, string>
        {
            ["zh"] = "中国代表团共有1274人，其中运动员815人，将参加38个大项的369个小项。",
            ["en"] = "The Chinese delegation has 1274 members, including 815 athletes, competing in 369 events across 38 sports.",
            ["fr"] = "La délégation chinoise compte 1274 membres, dont 815 athlètes, participant à 369 épreuves dans 38 sports.",
            ["es"] = "La delegación china tiene 1274 miembros, incluidos 815 atletas, que competirán en 369 pruebas de 38 deportes.",
            ["ru"] = "В состав китайской делегации входят 1274 человека, включая 815 спортсменов, которые выступят в 369 дисциплинах в 38 видах спорта.",
            ["ar"] = "يضم الوفد الصيني 1274 عضواً، منهم 815 رياضياً، يتنافسون في 369 مسابقة ضمن 38 رياضة.",
        };
        var failures = new List<string>();
        if (!articleResult.IsSuccess) failures.Add("Japanese article: " + articleResult.ErrorMessage);
        else if (articleResult.Segments.Count != article.Length ||
            articleResult.Segments.Any(text => text.Any(c => c is >= '\u3040' and <= '\u30ff')))
            failures.Add("Japanese article has an untranslated or missing paragraph.");
        foreach (var source in samples)
        foreach (var target in samples.Keys.Where(target => target != source.Key))
        {
            watch.Restart();
            var result = await provider.TranslateAsync(source.Value, source.Key, target);
            records.Add(new { source = source.Key, target, seconds = watch.Elapsed.TotalSeconds,
                result.IsSuccess, result.ErrorMessage, output = result.Text });
            if (!result.IsSuccess) failures.Add($"{source.Key} → {target}: {result.ErrorMessage}");
        }
        var report = Environment.GetEnvironmentVariable("SNAPCUT_TRANSLATION_BENCHMARK_REPORT");
        if (!string.IsNullOrWhiteSpace(report))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(report))!);
            await File.WriteAllTextAsync(report, JsonSerializer.Serialize(records, ReportOptions));
        }
        Assert.Empty(failures);
    }
}

public sealed class TranslationModelFactAttribute : FactAttribute
{
    public TranslationModelFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("SNAPCUT_RUN_TRANSLATION_MODEL_TESTS") != "1")
            Skip = "Requires the downloaded model and explicit SNAPCUT_RUN_TRANSLATION_MODEL_TESTS=1; no network download in tests.";
    }
}
