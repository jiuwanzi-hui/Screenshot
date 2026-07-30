using System.Collections.Concurrent;
using System.IO;
using BergamotTranslatorSharp;

namespace Screenshot.App.Text;

public sealed class OfflineTranslationProvider : ITranslationProvider
{
    private static readonly ConcurrentDictionary<string, TranslationEngine> Engines =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly OfflineTranslationModelManager _modelManager;
    private readonly IOfflineLanguageDetector _languageDetector;

    public OfflineTranslationProvider(OfflineTranslationModelManager modelManager)
        : this(modelManager, Cld3OfflineLanguageDetector.Shared)
    {
    }

    internal OfflineTranslationProvider(
        OfflineTranslationModelManager modelManager,
        IOfflineLanguageDetector languageDetector)
    {
        _modelManager = modelManager ??
            throw new ArgumentNullException(nameof(modelManager));
        _languageDetector = languageDetector ??
            throw new ArgumentNullException(nameof(languageDetector));
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

        var translated = segments.ToArray();
        var eligibleIndexes = Enumerable.Range(0, segments.Count)
            .Where(index => ShouldTranslateSegment(segments[index]))
            .ToArray();
        if (eligibleIndexes.Length == 0)
        {
            return new TranslationSegmentsResult(true, translated, null);
        }

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
                    targetCode);
                if (configurationPaths is null)
                {
                    return TranslationSegmentsResult.Failure(
                        $"已自动检测到{TranslationLanguageCatalog.GetDisplayName(sourceCode)}，" +
                        $"但尚未安装到{TranslationLanguageCatalog.GetDisplayName(targetCode)}" +
                        "所需的离线模型，请到设置的“翻译”页面下载当前目标语言包。");
                }

                var current = indexes.Select(index => segments[index]).ToArray();
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
                    translated[indexes[index]] = string.IsNullOrWhiteSpace(current[index])
                        ? segments[indexes[index]]
                        : current[index].Trim();
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
        var languageByIndex = new Dictionary<int, string>();
        foreach (var index in eligibleIndexes)
        {
            var detection = _languageDetector.Detect(segments[index]);
            var selected = SelectDetection(detection, combinedDetection);
            if (!selected.IsSuccess || selected.ErrorMessage is not null ||
                !TranslationLanguageCatalog.IsSupportedSource(selected.LanguageCode))
            {
                var preview = segments[index].Trim();
                if (preview.Length > 24)
                {
                    preview = preview[..24] + "…";
                }

                return new SourceLanguageResolution(
                    languageByIndex,
                    $"无法自动识别“{preview}”的源语言。" +
                    (selected.ErrorMessage ?? "请增加文字内容后重试。"));
            }

            languageByIndex[index] = selected.LanguageCode!;
        }

        return new SourceLanguageResolution(languageByIndex, null);
    }

    private static OfflineLanguageDetectionResult SelectDetection(
        OfflineLanguageDetectionResult segment,
        OfflineLanguageDetectionResult combined)
    {
        if (segment.IsSuccess &&
            TranslationLanguageCatalog.IsSupportedSource(segment.LanguageCode) &&
            (segment.IsReliable || segment.Confidence >= 0.70d))
        {
            return segment;
        }

        return combined.IsSuccess && combined.ErrorMessage is null &&
               TranslationLanguageCatalog.IsSupportedSource(combined.LanguageCode) &&
               (combined.IsReliable || combined.Confidence >= 0.70d)
            ? combined
            : segment;
    }

    private static bool ShouldTranslateSegment(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || !text.Any(char.IsLetter))
        {
            return false;
        }

        var value = text.Trim();
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
            uri.Scheme is "http" or "https" or "file")
        {
            return false;
        }

        if (value.Contains('\\') ||
            (!value.Any(char.IsWhiteSpace) && value.Contains('/')))
        {
            return false;
        }

        if (value.Any(char.IsWhiteSpace))
        {
            return true;
        }

        var extension = Path.GetExtension(value);
        return string.IsNullOrEmpty(extension) || extension.Length is < 2 or > 11 ||
               !extension.AsSpan(1).ToArray().All(char.IsLetterOrDigit);
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
