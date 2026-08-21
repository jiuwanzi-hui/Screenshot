using System.Collections.Concurrent;
using System.IO;
using BergamotTranslatorSharp;
using Screenshot.App.Core;

namespace Screenshot.App.Text;

public sealed class OfflineTranslationProvider : ITranslationProvider
{
    private static readonly TimeSpan EngineIdleTimeout = TimeSpan.FromSeconds(30);
    private static readonly object EngineLifecycleLock = new();
    private static readonly ConcurrentDictionary<string, TranslationEngine> Engines =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly System.Threading.Timer EngineUnloadTimer = new(
        _ => TryUnloadIdleEngines(),
        state: null,
        Timeout.InfiniteTimeSpan,
        Timeout.InfiniteTimeSpan);
    private static int _activeOperations;
    private readonly OfflineTranslationModelManager _modelManager;
    private readonly IOfflineLanguageDetector _languageDetector;
    private readonly OfflineTranslationQuality _quality;

    public OfflineTranslationProvider(
        OfflineTranslationModelManager modelManager,
        OfflineTranslationQuality quality = OfflineTranslationQuality.High)
        : this(modelManager, Cld3OfflineLanguageDetector.Shared, quality)
    {
    }

    internal OfflineTranslationProvider(
        OfflineTranslationModelManager modelManager,
        IOfflineLanguageDetector languageDetector,
        OfflineTranslationQuality quality = OfflineTranslationQuality.High)
    {
        _modelManager = modelManager ??
            throw new ArgumentNullException(nameof(modelManager));
        _languageDetector = languageDetector ??
            throw new ArgumentNullException(nameof(languageDetector));
        _quality = Enum.IsDefined(quality)
            ? quality
            : OfflineTranslationQuality.High;
    }

    public string Id => TranslationProviderFactory.OfflineProviderId;

    public async Task<TranslationResult> TranslateAsync(
        string text,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return TranslationResult.Failure("没有可翻译的文字。");
        }

        var result = await TranslateSegmentsAsync(
            [text],
            sourceLanguage,
            targetLanguage,
            cancellationToken);
        return result.IsSuccess
            ? new TranslationResult(true, result.Segments[0], null)
            : TranslationResult.Failure(result.ErrorMessage ?? "离线翻译失败。");
    }

    public async Task<TranslationSegmentsResult> TranslateSegmentsAsync(
        IReadOnlyList<string> segments,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(segments);
        if (segments.Count == 0 || segments.All(string.IsNullOrWhiteSpace))
        {
            return TranslationSegmentsResult.Failure("没有可翻译的文字。");
        }

        var targetCode = TranslationLanguageCatalog.NormalizeOfflineCode(
            targetLanguage);
        if (targetCode is null)
        {
            return TranslationSegmentsResult.Failure("请选择离线翻译目标语言。");
        }

        if (segments.All(segment =>
                TranslationTargetLanguageMatcher.IsAlreadyTargetLanguage(
                    segment,
                    targetLanguage)))
        {
            return new TranslationSegmentsResult(true, segments.ToArray(), null);
        }

        var translated = segments.ToArray();
        var eligibleIndexes = Enumerable.Range(0, segments.Count)
            .Where(index =>
                ShouldTranslateSegment(segments[index]) &&
                !TranslationTargetLanguageMatcher.IsAlreadyTargetLanguage(
                    segments[index],
                    targetLanguage))
            .ToArray();
        if (eligibleIndexes.Length == 0)
        {
            return new TranslationSegmentsResult(true, translated, null);
        }

        BeginEngineUse();
        try
        {
            var sources = ResolveSourceLanguages(
                segments,
                eligibleIndexes,
                sourceLanguage);
            if (sources.ErrorMessage is not null)
            {
                return TranslationSegmentsResult.Failure(sources.ErrorMessage);
            }

            foreach (var group in sources.LanguageByIndex
                         .Where(entry => !string.Equals(
                             entry.Value,
                             targetCode,
                             StringComparison.OrdinalIgnoreCase))
                         .GroupBy(entry => entry.Value, StringComparer.OrdinalIgnoreCase))
            {
                var sourceCode = group.Key;
                var indexes = group.Select(entry => entry.Key).ToArray();
                var configurationPaths = _modelManager.GetInstalledRoute(
                    sourceCode,
                    targetCode,
                    _quality);
                if (configurationPaths is null)
                {
                    return TranslationSegmentsResult.Failure(
                        $"已自动检测到{TranslationLanguageCatalog.GetDisplayName(sourceCode)}，" +
                        $"但尚未安装到{TranslationLanguageCatalog.GetDisplayName(targetCode)}" +
                        "所需的离线模型，请到设置的“翻译”页面下载当前目标语言包。");
                }

                var original = indexes.Select(index => segments[index]).ToArray();
                var termProtector = TranslationTermProtector.Create(original);
                var current = termProtector.Segments.ToArray();
                foreach (var configurationPath in configurationPaths)
                {
                    var engine = Engines.GetOrAdd(
                        configurationPath,
                        path => new TranslationEngine(path));
                    await engine.Gate.WaitAsync(cancellationToken);
                    try
                    {
                        current = await Task.Run(
                            () => engine.Service.Value.Translate(current),
                            cancellationToken);
                    }
                    finally
                    {
                        engine.Gate.Release();
                    }

                    if (current.Length != indexes.Length)
                    {
                        return TranslationSegmentsResult.Failure(
                            "离线翻译返回的分段数量不完整。");
                    }
                }

                for (var index = 0; index < indexes.Length; index++)
                {
                    var restored = termProtector.Restore(
                        index,
                        current[index]).Trim();
                    restored = TranslationTechnicalTokenRestorer.Restore(
                        original[index],
                        restored);
                    translated[indexes[index]] = string.IsNullOrWhiteSpace(restored)
                        ? segments[indexes[index]]
                        : restored;
                }
            }

            return new TranslationSegmentsResult(true, translated, null);
        }
        catch (OperationCanceledException)
        {
            return TranslationSegmentsResult.Failure("离线翻译已取消。");
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or DllNotFoundException or
                BadImageFormatException or EntryPointNotFoundException)
        {
            return TranslationSegmentsResult.Failure(
                "无法加载离线翻译模型，请返回设置重新下载语言包。");
        }
        finally
        {
            EndEngineUse();
        }
    }

    private static void BeginEngineUse()
    {
        lock (EngineLifecycleLock)
        {
            _activeOperations++;
            EngineUnloadTimer.Change(
                Timeout.InfiniteTimeSpan,
                Timeout.InfiniteTimeSpan);
        }
    }

    private static void EndEngineUse()
    {
        lock (EngineLifecycleLock)
        {
            _activeOperations--;
            if (_activeOperations == 0)
            {
                EngineUnloadTimer.Change(
                    EngineIdleTimeout,
                    Timeout.InfiniteTimeSpan);
            }
        }
    }

    private static void TryUnloadIdleEngines()
    {
        lock (EngineLifecycleLock)
        {
            if (_activeOperations != 0)
            {
                EngineUnloadTimer.Change(
                    TimeSpan.FromSeconds(5),
                    Timeout.InfiniteTimeSpan);
                return;
            }

            var retryNeeded = false;
            foreach (var entry in Engines.ToArray())
            {
                var engine = entry.Value;
                if (!engine.Gate.Wait(0))
                {
                    retryNeeded = true;
                    continue;
                }

                try
                {
                    if (Engines.TryRemove(
                            new KeyValuePair<string, TranslationEngine>(
                                entry.Key,
                                engine)) &&
                        engine.Service.IsValueCreated)
                    {
                        engine.Service.Value.Dispose();
                    }
                }
                finally
                {
                    engine.Gate.Release();
                }
            }

            if (retryNeeded)
            {
                EngineUnloadTimer.Change(
                    TimeSpan.FromSeconds(5),
                    Timeout.InfiniteTimeSpan);
            }
        }
    }

    private SourceLanguageResolution ResolveSourceLanguages(
        IReadOnlyList<string> segments,
        IReadOnlyList<int> eligibleIndexes,
        string sourceLanguage)
    {
        var configuredSource = TranslationLanguageCatalog.NormalizeOfflineCode(
            sourceLanguage);
        if (configuredSource is not null)
        {
            return new SourceLanguageResolution(
                eligibleIndexes.ToDictionary(index => index, _ => configuredSource),
                null);
        }

        var combinedDetection = _languageDetector.Detect(string.Join(
            Environment.NewLine,
            eligibleIndexes.Select(index => segments[index])));
        if (!combinedDetection.IsSuccess ||
            combinedDetection.ErrorMessage is not null ||
            !TranslationLanguageCatalog.IsSupportedSource(
                combinedDetection.LanguageCode))
        {
            return new SourceLanguageResolution(
                new Dictionary<int, string>(),
                combinedDetection.ErrorMessage ??
                "无法可靠识别整张截图的源语言，请手动选择源语言。");
        }

        var languageByIndex = new Dictionary<int, string>();
        foreach (var index in eligibleIndexes)
        {
            var detection = _languageDetector.Detect(segments[index]);
            // A screenshot contains many short UI fragments. Per-line CLD3
            // guesses are noisy and one bad guess used to abort the entire
            // capture. Use the reliable whole-capture language by default;
            // only a distinctive-script, fully reliable line may override it.
            var letters = segments[index].Count(char.IsLetter);
            var detectedLanguage = detection.IsSuccess &&
                detection.ErrorMessage is null &&
                detection.IsReliable &&
                detection.Confidence >= 0.85d &&
                letters >= 20 &&
                TranslationLanguageCatalog.IsSupportedSource(
                    detection.LanguageCode)
                    ? detection.LanguageCode!
                    : combinedDetection.LanguageCode!;
            if (TranslationTargetLanguageMatcher.HasLatinNaturalLanguageClause(
                    segments[index]) &&
                string.Equals(
                    detectedLanguage,
                    "zh",
                    StringComparison.OrdinalIgnoreCase))
            {
                detectedLanguage = "en";
            }

            languageByIndex[index] = detectedLanguage;
        }

        return new SourceLanguageResolution(languageByIndex, null);
    }

    private static bool ShouldTranslateSegment(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || !text.Any(char.IsLetter))
        {
            return false;
        }

        return !TranslationTargetLanguageMatcher.IsLikelyInvariant(text);
    }

    private sealed record SourceLanguageResolution(
        IReadOnlyDictionary<int, string> LanguageByIndex,
        string? ErrorMessage);

    private sealed class TranslationEngine
    {
        public TranslationEngine(string configurationPath)
        {
            Service = new Lazy<BlockingService>(
                () => new BlockingService(configurationPath),
                LazyThreadSafetyMode.ExecutionAndPublication);
        }

        public Lazy<BlockingService> Service { get; }

        public SemaphoreSlim Gate { get; } = new(1, 1);
    }
}
