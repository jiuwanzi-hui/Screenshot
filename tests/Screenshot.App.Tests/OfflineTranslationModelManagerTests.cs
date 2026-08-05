using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using Screenshot.App.Core;
using Screenshot.App.Text;

namespace Screenshot.App.Tests;

public sealed class OfflineTranslationModelManagerTests : IDisposable
{
    private readonly string _testDirectory = Path.Combine(
        Path.GetTempPath(),
        "Screenshot.App.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task DownloadsValidatesAndInstallsPackTransactionally()
    {
        var firstContent = Encoding.UTF8.GetBytes("first offline model");
        var secondContent = Encoding.UTF8.GetBytes("second offline model");
        var firstCompressed = Compress(firstContent);
        var secondCompressed = Compress(secondContent);
        var directions = new[]
        {
            new OfflineTranslationDirection(
                "zh-en",
                "中文 → English",
                [CreateFile("first.gz", "first.bin", firstCompressed, firstContent)],
                "relative-paths: true\n",
                "test-zh-en-v1"),
            new OfflineTranslationDirection(
                "en-zh",
                "English → 中文",
                [CreateFile("second.gz", "second.bin", secondCompressed, secondContent)],
                "relative-paths: true\n",
                "test-en-zh-v1"),
        };
        var plan = new OfflineTranslationModelPlan(
            "zh",
            "zh",
            "测试多语言路线",
            "https://models.example/",
            directions);
        var handler = new ModelDownloadHandler(new Dictionary<string, byte[]>
        {
            ["/first.gz"] = firstCompressed,
            ["/second.gz"] = secondCompressed,
        });
        using var client = new HttpClient(handler);
        using var manager = new OfflineTranslationModelManager(
            _testDirectory,
            client);
        var progressValues = new List<OfflineTranslationDownloadProgress>();

        var result = await manager.InstallAsync(
            plan,
            new SynchronousProgress<OfflineTranslationDownloadProgress>(
                progressValues.Add));

        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.True(manager.GetStatus(plan).IsInstalled);
        Assert.Equal(0, manager.GetStatus(plan).DownloadSize);
        Assert.Equal(0, manager.GetStatus(plan).InstalledSize);
        Assert.Equal(
            firstContent,
            File.ReadAllBytes(Path.Combine(
                manager.ModelsDirectory,
                "zh-en",
                "first.bin")));
        Assert.Equal(
            secondContent,
            File.ReadAllBytes(Path.Combine(
                manager.ModelsDirectory,
                "en-zh",
                "second.bin")));
        Assert.EndsWith(
            Path.Combine("zh-en", "config.yml"),
            manager.GetConfigurationPath("zh-en"),
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(progressValues, value =>
            value.DownloadedBytes == plan.DownloadSize);
        Assert.Equal(2, handler.RequestCount);

        Directory.Delete(manager.ModelsDirectory, recursive: true);

        Assert.False(manager.GetStatus(plan).IsInstalled);
        Assert.True(manager.GetStatus(plan).DownloadSize > 0);
    }

    [Fact]
    public async Task ReplacesAnOutdatedPackWhenItsFilesAreReadOnly()
    {
        var installed = Encoding.UTF8.GetBytes("updated offline model");
        var compressed = Compress(installed);
        var direction = new OfflineTranslationDirection(
            "en-zh",
            "English → 中文",
            [CreateFile("model.gz", "model.bin", compressed, installed)],
            "relative-paths: true\n",
            "test-en-zh-v2");
        var plan = new OfflineTranslationModelPlan(
            "en",
            "zh",
            "English → 中文",
            "https://models.example/",
            [direction]);
        var handler = new ModelDownloadHandler(new Dictionary<string, byte[]>
        {
            ["/model.gz"] = compressed,
        });
        using var client = new HttpClient(handler);
        using var manager = new OfflineTranslationModelManager(
            _testDirectory,
            client);
        var outdatedDirectory = Path.Combine(manager.ModelsDirectory, "en-zh");
        Directory.CreateDirectory(outdatedDirectory);
        var outdatedFile = Path.Combine(outdatedDirectory, "old-model.bin");
        File.WriteAllText(outdatedFile, "outdated");
        File.SetAttributes(outdatedFile, FileAttributes.ReadOnly);

        var result = await manager.InstallAsync(plan);

        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.True(manager.GetStatus(plan).IsInstalled);
        Assert.Equal(
            installed,
            File.ReadAllBytes(Path.Combine(
                manager.ModelsDirectory,
                "en-zh",
                "model.bin")));
        Assert.Empty(Directory.EnumerateDirectories(
            manager.ModelsDirectory,
            ".*.old"));
    }

    [Fact]
    public async Task RejectsAFileThatDoesNotMatchTheManifest()
    {
        var expected = Encoding.UTF8.GetBytes("expected model");
        var actual = Encoding.UTF8.GetBytes("different model");
        var compressed = Compress(actual);
        var direction = new OfflineTranslationDirection(
            "zh-en",
            "中文 → English",
            [new OfflineTranslationModelFile(
                "model.gz",
                "model.bin",
                compressed.Length,
                actual.Length,
                Convert.ToHexString(SHA256.HashData(expected)))],
            "relative-paths: true\n",
            "test-zh-en-v1");
        var plan = new OfflineTranslationModelPlan(
            "zh",
            "en",
            "中文 → English",
            "https://models.example/",
            [direction]);
        var handler = new ModelDownloadHandler(
            new Dictionary<string, byte[]> { ["/model.gz"] = compressed });
        using var client = new HttpClient(handler);
        using var manager = new OfflineTranslationModelManager(
            _testDirectory,
            client);

        var result = await manager.InstallAsync(plan);

        Assert.False(result.IsSuccess);
        Assert.Contains("完整性校验", result.ErrorMessage);
        Assert.Equal(1, handler.RequestCount);
        Assert.False(manager.GetStatus(plan).IsInstalled);
        Assert.False(Directory.Exists(Path.Combine(
            manager.ModelsDirectory,
            "zh-en")));
    }

    [Fact]
    public async Task RetriesATransientDownloadFailureAutomatically()
    {
        var installed = Encoding.UTF8.GetBytes("resilient offline model");
        var compressed = Compress(installed);
        var file = CreateFile("model.gz", "model.bin", compressed, installed);
        var plan = new OfflineTranslationModelPlan(
            "en",
            "zh",
            "English → 中文",
            "https://models.example/",
            [new OfflineTranslationDirection(
                "en-zh",
                "English → 中文",
                [file],
                "relative-paths: true\n",
                "test-en-zh-v1")]);
        var handler = new TransientFailureDownloadHandler(compressed);
        using var client = new HttpClient(handler);
        using var manager = new OfflineTranslationModelManager(
            _testDirectory,
            client);
        var progressValues = new List<OfflineTranslationDownloadProgress>();

        var result = await manager.InstallAsync(
            plan,
            new SynchronousProgress<OfflineTranslationDownloadProgress>(
                progressValues.Add));

        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Equal(2, handler.RequestCount);
        Assert.Contains(progressValues, value =>
            value.CurrentFileName.Contains("自动重试", StringComparison.Ordinal));
        Assert.Equal(
            installed,
            File.ReadAllBytes(Path.Combine(
                manager.ModelsDirectory,
                "en-zh",
                "model.bin")));
    }

    [Fact]
    public void LegacyOfflineModeMigratesToOfflineFirstAutomaticFallback()
    {
        var settings = AppSettings.CreateDefault() with
        {
            SettingsVersion = 2,
            TranslationMode = TranslationMode.Offline,
            SendTextToOnlineTranslation = false,
        };
        using var client = new HttpClient(new ModelDownloadHandler(
            new Dictionary<string, byte[]>()));
        using var manager = new OfflineTranslationModelManager(
            _testDirectory,
            client);

        var provider = TranslationProviderFactory.Create(
            settings,
            new EmptyCredentialStore(),
            client,
            manager);

        var ordered = Assert.IsType<OrderedTranslationProvider>(provider);
        Assert.Equal(
            [
                TranslationProviderFactory.OfflineProviderId,
                TranslationProviderFactory.OpenAiCompatibleProviderId,
            ],
            ordered.ProviderIds);
    }

    [Fact]
    public void LegacyOnlineConsentMigratesToOnlineFirstAutomaticFallback()
    {
        var settings = (AppSettings.CreateDefault() with
        {
            SettingsVersion = 1,
            TranslationMode = TranslationMode.Disabled,
            SendTextToOnlineTranslation = true,
        }).Normalize();

        Assert.Equal(TranslationMode.Automatic, settings.TranslationMode);
        Assert.True(settings.SendTextToOnlineTranslation);
        Assert.Equal(5, settings.SettingsVersion);
        Assert.Equal(
            [TranslationProviderKind.Online, TranslationProviderKind.Offline],
            settings.TranslationProviderPriority);
    }

    [Fact]
    public void MultilingualRoutesNormalizeOcrTagsAndPivotThroughEnglish()
    {
        Assert.Equal("zh", TranslationLanguageCatalog.NormalizeOfflineCode("zh-Hans"));
        Assert.Equal("zh_hant", TranslationLanguageCatalog.NormalizeOfflineCode("zh-TW"));
        Assert.Equal("ja", TranslationLanguageCatalog.NormalizeOfflineCode("ja-JP"));
        Assert.Equal(
            ["ja-en", "en-zh"],
            TranslationLanguageCatalog.BuildRoute("ja-JP", "zh-Hans"));
        Assert.Equal(
            ["en-fr"],
            TranslationLanguageCatalog.BuildRoute("en-US", "fr-FR"));
        Assert.True(
            TranslationLanguageCatalog.OfflineTargetLanguages.Count >= 53);
    }

    [Theory]
    [InlineData(OfflineTranslationQuality.Fast, "beam-size: 1")]
    [InlineData(OfflineTranslationQuality.High, "beam-size: 4")]
    [InlineData(OfflineTranslationQuality.Ultra, "beam-size: 8")]
    public void OfflineQualityProducesTheExpectedBeamSearchConfiguration(
        OfflineTranslationQuality quality,
        string expectedBeamSize)
    {
        var configuration = OfflineTranslationModelCatalog.CreateConfiguration(
            "model.bin",
            "source.spm",
            "target.spm",
            null);

        var adjusted = OfflineTranslationModelCatalog.ApplyQuality(
            configuration,
            quality);

        Assert.Contains(expectedBeamSize, adjusted);
        Assert.Equal(1, adjusted.Split(expectedBeamSize).Length - 1);
    }

    [Fact]
    public async Task OfflineProviderAutoDetectsSourceLanguage()
    {
        using var manager = new OfflineTranslationModelManager(_testDirectory);
        var provider = new OfflineTranslationProvider(
            manager,
            new StubLanguageDetector("ja"));

        var result = await provider.TranslateAsync("こんにちは", "auto", "zh-Hans");

        Assert.False(result.IsSuccess);
        Assert.Contains("日本語", result.ErrorMessage);
        Assert.Contains("目标语言包", result.ErrorMessage);
    }

    [Fact]
    public async Task AutoDetectedTargetLanguageDoesNotRequireAModel()
    {
        using var manager = new OfflineTranslationModelManager(_testDirectory);
        var provider = new OfflineTranslationProvider(
            manager,
            new StubLanguageDetector("zh"));

        var result = await provider.TranslateAsync("你好，世界。", "auto", "zh-Hans");

        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Equal("你好，世界。", result.Text);
    }

    [Fact]
    public async Task TechnicalFilePathsArePreservedWithoutLanguageDetection()
    {
        using var manager = new OfflineTranslationModelManager(_testDirectory);
        var detector = new StubLanguageDetector("mg");
        var provider = new OfflineTranslationProvider(manager, detector);
        string[] paths =
        [
            "electron/main.ts",
            "electron/migration.ts",
            "electron/preload.ts",
            "AGENTS.md",
        ];

        var result = await provider.TranslateSegmentsAsync(
            paths,
            "auto",
            "zh-Hans");

        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Equal(paths, result.Segments);
        Assert.Equal(0, detector.CallCount);
    }

    [Fact]
    public void AutoDetectPackCoversEverySupportedSourceRoute()
    {
        var directions = TranslationLanguageCatalog
            .BuildAutoDetectPackDirections("zh-Hans");

        Assert.Contains("en-zh", directions);
        Assert.Contains("ja-en", directions);
        Assert.Contains("fr-en", directions);
        Assert.DoesNotContain("zh-en", directions);
        Assert.Equal(
            TranslationLanguageCatalog.OfflineSourceCodes.Count - 1,
            directions.Count);
    }

    [Theory]
    [InlineData("Hello, this is an offline language detector test.", "en")]
    [InlineData("こんにちは。これは日本語の文章です。", "ja")]
    [InlineData("Bonjour, ceci est une phrase française.", "fr")]
    [InlineData("Привет, это предложение на русском языке.", "ru")]
    [InlineData("这是一个简体中文语言检测测试。", "zh")]
    [InlineData("這是一個繁體中文語言檢測測試。", "zh_hant")]
    public void Cld3DetectsCommonOfflineSourceLanguages(
        string text,
        string expectedLanguage)
    {
        var result = Cld3OfflineLanguageDetector.Shared.Detect(text);

        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Equal(expectedLanguage, result.LanguageCode);
    }

    [Fact]
    [Trait("Category", "ExternalModel")]
    public async Task OfficialMozillaTargetPackCanBeCalculatedWhenEnabled()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(
                    "SNAPCUT_RUN_OFFLINE_MODEL_TEST"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        using var manager = new OfflineTranslationModelManager(_testDirectory);
        var result = await manager.PrepareTargetPlanAsync("zh-Hans");

        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.NotNull(result.Plan);
        Assert.Equal(
            TranslationLanguageCatalog.OfflineSourceCodes.Count - 1,
            result.Plan!.Directions.Count);
        Assert.True(result.Plan.DownloadSize > 0);
        Assert.True(result.Plan.InstalledSize > result.Plan.DownloadSize);
    }

    [Fact]
    [Trait("Category", "ExternalModel")]
    public async Task OfficialMozillaPackTranslatesBothDirectionsWhenEnabled()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(
                    "SNAPCUT_RUN_OFFLINE_MODEL_TEST"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        using var manager = new OfflineTranslationModelManager(_testDirectory);
        var zhEnPlanResult = await manager.PreparePlanAsync("zh-Hans", "en");
        var enZhPlanResult = await manager.PreparePlanAsync("en", "zh-Hans");
        var jaZhPlanResult = await manager.PreparePlanAsync("ja-JP", "zh-Hans");
        Assert.True(zhEnPlanResult.IsSuccess, zhEnPlanResult.ErrorMessage);
        Assert.True(enZhPlanResult.IsSuccess, enZhPlanResult.ErrorMessage);
        Assert.True(jaZhPlanResult.IsSuccess, jaZhPlanResult.ErrorMessage);
        Assert.Equal(
            ["ja-en", "en-zh"],
            jaZhPlanResult.Plan!.Directions.Select(direction => direction.Id));
        var zhEnInstallation = await manager.InstallAsync(zhEnPlanResult.Plan!);
        var enZhInstallation = await manager.InstallAsync(enZhPlanResult.Plan!);
        var jaZhInstallation = await manager.InstallAsync(jaZhPlanResult.Plan!);
        Assert.True(zhEnInstallation.IsSuccess, zhEnInstallation.ErrorMessage);
        Assert.True(enZhInstallation.IsSuccess, enZhInstallation.ErrorMessage);
        Assert.True(jaZhInstallation.IsSuccess, jaZhInstallation.ErrorMessage);
        var provider = new OfflineTranslationProvider(manager);

        var english = await provider.TranslateAsync(
            "你好，世界。",
            "zh-Hans",
            "en");
        var chinese = await provider.TranslateAsync(
            "Hello, world.",
            "en",
            "zh-Hans");
        var japaneseToChinese = await provider.TranslateAsync(
            "こんにちは、世界。",
            "ja-JP",
            "zh-Hans");

        Assert.True(english.IsSuccess, english.ErrorMessage);
        Assert.True(chinese.IsSuccess, chinese.ErrorMessage);
        Assert.True(japaneseToChinese.IsSuccess, japaneseToChinese.ErrorMessage);
        Assert.NotEqual("你好，世界。", english.Text);
        Assert.NotEqual("Hello, world.", chinese.Text);
        Assert.Contains(chinese.Text, character => character is >= '\u3400' and <= '\u9FFF');
        Assert.Contains(
            japaneseToChinese.Text,
            character => character is >= '\u3400' and <= '\u9FFF');
    }

    private static OfflineTranslationModelFile CreateFile(
        string downloadPath,
        string installedFileName,
        byte[] compressed,
        byte[] installed)
    {
        return new OfflineTranslationModelFile(
            downloadPath,
            installedFileName,
            compressed.Length,
            installed.Length,
            Convert.ToHexString(SHA256.HashData(installed)));
    }

    private static byte[] Compress(byte[] value)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, true))
        {
            gzip.Write(value);
        }

        return output.ToArray();
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, recursive: true);
        }
    }

    private sealed class ModelDownloadHandler(
        IReadOnlyDictionary<string, byte[]> responses) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (!responses.TryGetValue(path, out var content))
            {
                return Task.FromResult(new HttpResponseMessage(
                    HttpStatusCode.NotFound));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(content),
            });
        }
    }

    private sealed class TransientFailureDownloadHandler(byte[] response)
        : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            if (RequestCount == 1)
            {
                throw new HttpRequestException("模拟网络连接中断。");
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(response),
            });
        }
    }

    private sealed class SynchronousProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed class EmptyCredentialStore : ITranslationCredentialStore
    {
        public string? GetApiKey(string providerId) => null;

        public void SetApiKey(string providerId, string? apiKey)
        {
        }
    }

    private sealed class StubLanguageDetector(string languageCode)
        : IOfflineLanguageDetector
    {
        public int CallCount { get; private set; }

        public OfflineLanguageDetectionResult Detect(string text)
        {
            CallCount++;
            return new OfflineLanguageDetectionResult(languageCode, 1, true);
        }
    }
}
