using System.Globalization;
using System.Net.Http;
using Screenshot.App.Core;

namespace Screenshot.App.Text;

public sealed class LocalLargeModelTranslationProvider : ITranslationProvider
{
    private readonly LocalLargeTranslationModelManager _modelManager;
    private readonly OfflineTranslationProvider? _offlineRepair;
    // Cold model loading is separate from generation. CPU inference remains
    // cancellable instead of returning unchanged text after a fixed ten seconds.
    internal static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(90);
    internal static readonly TimeSpan TranslationTimeout = TimeSpan.FromMinutes(3);
    internal static TimeSpan TotalTimeout => StartupTimeout + TranslationTimeout;
    public string DisplayName => _modelManager.DisplayName;

    public LocalLargeModelTranslationProvider(LocalLargeTranslationModelManager? modelManager = null,
        OfflineTranslationProvider? offlineRepair = null)
    {
        _modelManager = modelManager ?? LocalLargeTranslationModelManager.Shared;
        _offlineRepair = offlineRepair;
    }

    // Retained for settings and provider availability compatibility.
    public string Id => TranslationProviderFactory.LocalLargeModelProviderId;

    public async Task<TranslationResult> TranslateAsync(string text, string sourceLanguage,
        string targetLanguage, CancellationToken cancellationToken = default)
    {
        var result = await TranslateSegmentsAsync([text], sourceLanguage, targetLanguage, cancellationToken);
        return result.IsSuccess
            ? new(result.FailedSegmentIndices?.Contains(0) != true, result.Segments[0], result.ErrorMessage)
                { Notice = result.Notice }
            : TranslationResult.Failure(result.ErrorMessage ?? "离线翻译失败。");
    }

    public async Task<TranslationSegmentsResult> TranslateSegmentsAsync(IReadOnlyList<string> segments,
        string sourceLanguage, string targetLanguage, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(segments);
        if (segments.Count == 0 || segments.All(string.IsNullOrWhiteSpace))
            return TranslationSegmentsResult.Failure("没有可翻译的文字。");

        var source = TranslationLanguageCatalog.NormalizeOfflineCode(sourceLanguage);
        var modelDetectsSource = false;
        if (source is null)
        {
            var detection = Cld3OfflineLanguageDetector.Shared.Detect(string.Join("\n", segments.Select(TranslationTextNormalizer.Normalize)));
            if (!detection.IsSuccess || detection.ErrorMessage is not null)
            {
                // Short UI titles and proper names often defeat CLD3. A
                // multilingual generative model can translate them without a
                // guessed source code; retain validation of its actual output.
                source = "auto";
                modelDetectsSource = true;
            }
            else source = detection.LanguageCode!;
        }
        var target = TranslationLanguageCatalog.NormalizeOfflineCode(targetLanguage);
        if (target is null)
            return TranslationSegmentsResult.Failure("请选择翻译目标语言。");

        var parts = segments.SelectMany((segment, index) =>
        {
            var segmentSource = source;
            if (TranslationLanguageCatalog.NormalizeOfflineCode(sourceLanguage) is null)
            {
                var detection = Cld3OfflineLanguageDetector.Shared.Detect(TranslationTextNormalizer.Normalize(segment));
                // A multilingual sidebar must not make English body text Japanese.
                if (detection.ErrorMessage is null && detection.IsReliable && detection.Confidence >= 0.8)
                    segmentSource = detection.LanguageCode ?? source;
                if (segmentSource == "zh" && TranslationTargetLanguageMatcher.HasTranslatableLatinWords(segment))
                    segmentSource = "en";
            }
            return TranslationTargetLanguageMatcher.ShouldTranslate(segment, segmentSource, target)
                ? SplitText(segment).Select(text => (Index: index, Text: text, Source: segmentSource))
                : [];
        }).ToArray();
        if (parts.Length == 0) return new(true, segments.ToArray(), null);
        if (!_modelManager.GetStatus().IsInstalled)
            return TranslationSegmentsResult.Failure(
                $"{TranslationLanguageCatalog.GetDisplayName(source)} → {TranslationLanguageCatalog.GetDisplayName(target)}：" +
                $"需要下载 {_modelManager.DisplayName} 离线多语言模型（模型约 {_modelManager.ModelDownloadSize / 1073741824d:0.00} GiB）。" +
                "请打开设置 → 翻译 → 下载多语言模型；中、英、法、西、俄、阿及日语共用一份，安装后无需联网。");

        if (modelDetectsSource)
            TranslationProgress.Report($"源语言暂不确定，由 {_modelManager.DisplayName} 自动判断并翻译…");

        using var timeout = new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        var output = new string?[parts.Length];
        var failures = new string?[parts.Length];
        var repaired = 0;
        var repairAttempts = 0;
        var usedOfflineBackup = 0;
        var preparationElapsed = TimeSpan.Zero;
        var inferenceElapsed = TimeSpan.Zero;
        string? interruption = null;
        var requests = Enumerable.Range(0, parts.Length)
            .GroupBy(index => (parts[index].Text, parts[index].Source))
            .OrderByDescending(group => group.Key.Text.Length).ToArray();
        try
        {
            await _modelManager.InferenceSession.RunAsync(async (_, token) =>
            {
                timeout.CancelAfter(TranslationTimeout);
                preparationElapsed = _modelManager.InferenceSession.PreparationElapsed;
                var inferenceTimer = System.Diagnostics.Stopwatch.StartNew();
                TranslationProgress.Report($"{_modelManager.DisplayName} 正在翻译 {parts.Length} 个片段 · " +
                    _modelManager.InferenceSession.RuntimeDescription);
                await Parallel.ForEachAsync(requests, new ParallelOptions
                {
                    MaxDegreeOfParallelism = _modelManager.InferenceSession.Parallelism,
                    CancellationToken = token,
                }, async (request, partToken) =>
                {
                    var part = parts[request.First()];
                    try
                    {
                        var translated = await TranslatePartAsync(part.Text, part.Source, target, partToken).ConfigureAwait(false);
                        var failure = TranslationCompletenessValidator.Validate(part.Text, translated, target);
                        foreach (var index in request)
                        {
                            failures[index] = failure;
                            if (failure is null) output[index] = translated;
                        }
                        if (failure is null)
                            foreach (var segmentIndex in request.Select(index => parts[index].Index).Distinct())
                            {
                                var indices = Enumerable.Range(0, parts.Length).Where(i => parts[i].Index == segmentIndex).ToArray();
                                if (indices.All(i => output[i] is not null))
                                    TranslationSegmentProgress.Report(segments[segmentIndex], string.Join('\n', indices.Select(i => output[i])));
                            }
                    }
                    catch (Exception exception) when (exception is HttpRequestException or System.IO.IOException or InvalidOperationException)
                    {
                        foreach (var index in request) failures[index] = exception.Message;
                    }
                }).ConfigureAwait(false);
                if (requests.Any(request => output[request.First()] is null))
                    TranslationProgress.Report($"正在校验并补译未完成内容 · {_modelManager.InferenceSession.RuntimeDescription}");
                // Finish the first pass before repairs: one difficult paragraph
                // must not consume the budget of the rest of the screenshot.
                await Parallel.ForEachAsync(requests.Where(request => output[request.First()] is null),
                    new ParallelOptions { MaxDegreeOfParallelism = Math.Min(4, _modelManager.InferenceSession.Parallelism), CancellationToken = token },
                    async (request, partToken) =>
                {
                    var part = parts[request.First()];
                    try
                    {
                        Interlocked.Increment(ref repairAttempts);
                        var retry = await LocalTranslationRepair.RetryAsync(part.Text, target,
                            (sentence, retryToken) => TranslatePartAsync(sentence, part.Source, target, retryToken), partToken)
                            .ConfigureAwait(false);
                        if (retry is null && _offlineRepair?.HasInstalledRoute(part.Source, target) == true)
                        {
                            var backup = await _offlineRepair.TranslateSegmentsAsync(
                                [part.Text], part.Source, target, partToken).ConfigureAwait(false);
                            if (backup.IsSuccess && backup.Segments.Count == 1 &&
                                TranslationCompletenessValidator.Validate(part.Text, backup.Segments[0], target) is null)
                            {
                                retry = backup.Segments[0];
                                Interlocked.Increment(ref usedOfflineBackup);
                            }
                        }
                        if (retry is not null)
                        {
                            foreach (var index in request) { output[index] = retry; failures[index] = null; }
                            Interlocked.Increment(ref repaired);
                        }
                    }
                    catch (Exception exception) when (exception is HttpRequestException or System.IO.IOException or InvalidOperationException)
                    {
                        foreach (var index in request) failures[index] += " 补译失败：" + exception.Message;
                    }
                }).ConfigureAwait(false);
                inferenceElapsed = inferenceTimer.Elapsed;
                return true;
            }, linked.Token, requests.Length).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            interruption = "本机离线模型达到本次翻译时间限制，未完成的段落保留原文。";
        }
        catch (Exception exception) when (exception is HttpRequestException or System.IO.IOException or
            InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception)
        {
            interruption = $"多语言离线翻译失败：{exception.Message}";
        }
        var result = CombineParts(segments, parts.Select(part => part.Index).ToArray(), output, failures, interruption,
            _modelManager.DisplayName);
        if (modelDetectsSource)
            result = result with { Notice = result.Notice + " 源语言由所选离线模型自动判断；短标题和专有名称的译法请结合上下文核对。" };
        if (!string.IsNullOrWhiteSpace(_modelManager.InferenceSession.RuntimeDescription))
            result = result with { Notice = result.Notice + " 运行方式：" + _modelManager.InferenceSession.RuntimeDescription + "。" };
        if (inferenceElapsed > TimeSpan.Zero)
            result = result with { Notice = result.Notice +
                $" 本次模型准备／加载 {preparationElapsed.TotalSeconds:F1} 秒，翻译／校验 {inferenceElapsed.TotalSeconds:F1} 秒。" };
        if (repaired > 0)
            result = result with { Notice = result.Notice + $" 已自动补译 {repaired} 个未完成片段。" +
                (usedOfflineBackup > 0 ? "补译使用已安装的 Mozilla 离线语言包，全程在本机处理。" : string.Empty) };
        if (result.IsSuccess && result.FailedSegmentIndices is { Count: > 0 })
            result = result with { ErrorMessage = result.ErrorMessage +
                (repairAttempts > 0 ? " 已尝试离线补译；可再次补译未完成的内容。" : " 可再次补译未完成的内容。") +
                "设置 → 翻译可查看并下载可用的 Mozilla 离线语言包作为补译备用，无需配置在线模型。" };
        return result;
    }

    internal static TranslationSegmentsResult CombineParts(IReadOnlyList<string> segments,
        IReadOnlyList<int> indices, IReadOnlyList<string?> output, IReadOnlyList<string?> failures, string? interruption,
        string modelName = "TranslateGemma 4B")
    {
        var translated = segments.ToArray();
        var failed = new List<int>();
        foreach (var group in Enumerable.Range(0, indices.Count).GroupBy(index => indices[index]))
        {
            if (group.Any(index => output[index] is null)) failed.Add(group.Key);
            else translated[group.Key] = string.Join("\n", group.Select(index => output[index]));
        }
        var reasons = failures.Where(failure => failure is not null).Distinct(StringComparer.Ordinal).Take(3);
        // A worker that never produced a valid part failed to run. Preserve its
        // resource/loading error instead of treating the original text as output.
        var interruptedBeforeOutput = interruption is not null && output.All(part => part is null);
        return new TranslationSegmentsResult(!interruptedBeforeOutput, translated, failed.Count == 0 ? null :
            interruptedBeforeOutput ? interruption :
            $"本机离线模型 {modelName}：{failed.Count} 段未完成，已保留原文。" +
            interruption + string.Join("；", reasons))
        {
            FailedSegmentIndices = failed,
            Notice = $"{(failed.Count == translated.Length ? "已尝试" : "实际使用")}：本机离线模型 {modelName}。",
        };
    }

    internal static IReadOnlyList<string> SplitText(string text, int maximumCharacters = 600)
    {
        var pieces = new List<string>();
        for (var start = 0; start < text.Length;)
        {
            var length = Math.Min(maximumCharacters, text.Length - start);
            if (start + length < text.Length)
            {
                var boundary = -1;
                for (var i = start + length - 1; i >= start + length / 2; i--)
                {
                    if (text[i] is '。' or '！' or '？' or '\n' ||
                        text[i] is '.' or '!' or '?' && i + 1 < text.Length && char.IsWhiteSpace(text[i + 1]))
                    {
                        boundary = i + 1;
                        break;
                    }
                }
                if (boundary < 0)
                {
                    for (var i = start + length - 1; i >= start + length / 2; i--)
                        if (char.IsWhiteSpace(text[i])) { boundary = i + 1; break; }
                }
                if (boundary > start) length = boundary - start;
                if (char.IsHighSurrogate(text[start + length - 1])) length--;
            }
            pieces.Add(text.Substring(start, length));
            start += length;
        }
        return pieces;
    }

    private async Task<string> TranslatePartAsync(string text, string sourceCode, string targetCode, CancellationToken token)
    {
        var fields = new TranslationTechnicalTokens(text);
        var prompt = _modelManager.IsLightweight
            ? CreateLightweightPrompt(fields.Text, targetCode, fields.Instructions)
            : CreatePrompt(fields.Text, sourceCode, targetCode, fields.Instructions);
        var output = await _modelManager.InferenceSession.CompleteAsync(prompt, 1536, token).ConfigureAwait(false);
        return fields.TryRestore(output, out var restored) ? restored :
            throw new InvalidOperationException("模型遗漏或改写了版本号、文件名或路径，未将该结果作为完整译文。");
    }

    internal static string CreateLightweightPrompt(string text, string targetCode, string literalInstructions = "")
    {
        var target = targetCode switch
        {
            "zh" => "Simplified Chinese", "zh_hant" => "Traditional Chinese",
            _ => CultureInfo.GetCultureInfo(targetCode).EnglishName,
        };
        return "<｜hy_begin▁of▁sentence｜><｜hy_User｜>" +
            $"Translate the following text into {target}. " + MixedTextInstruction(text, targetCode) + literalInstructions +
            "Note that you should only output the translated result without any additional explanation:\n\n" +
            TranslationTextNormalizer.Normalize(text).Trim() + "<｜hy_Assistant｜>";
    }

    private static string MixedTextInstruction(string text, string targetCode) =>
        Realtime.RealtimeTranslationContext.Instructions +
        (targetCode is "zh" or "zh_hant" && text.Any(c => c is >= '\u3400' and <= '\u9fff') &&
        TranslationTargetLanguageMatcher.HasTranslatableLatinWords(text)
            ? "This is mixed Chinese and English text. Translate the English words and technical labels into Chinese, including capitalized words. Keep existing Chinese and acronyms unchanged. "
            : string.Empty);

    internal static string CreatePrompt(string text, string sourceCode, string targetCode, string literalInstructions = "")
    {
        static (string Code, string Name) Language(string code)
        {
            code = code switch { "zh" => "zh-Hans", "zh_hant" => "zh-Hant", _ => code };
            var name = code.StartsWith("zh", StringComparison.Ordinal) ? "Chinese"
                : CultureInfo.GetCultureInfo(code).EnglishName;
            return (code, name);
        }
        var target = Language(targetCode);
        if (sourceCode == "auto")
        {
            if (TranslationTextNormalizer.ExpandCompoundLabel(text) is not null)
            {
                return "<bos><start_of_turn>user\nYou are a professional translator. " +
                    $"Identify the source language and translate the following label into {target.Name} ({target.Code}). " +
                    $"Produce only the {target.Name} translation, without any additional explanations or commentary. " +
                    MixedTextInstruction(text, targetCode) + literalInstructions + "\n\n\n" +
                    TranslationTextNormalizer.Normalize(text).Trim() + "<end_of_turn>\n<start_of_turn>model\n";
            }
            return "<bos><start_of_turn>user\nYou are a professional translator. " +
                $"Identify the source language of the text and translate it into {target.Name} ({target.Code}). " +
                "Translate every part of the text, including names, possessives, titles and numbers. " +
                "Do not summarize or omit unfamiliar terms; transliterate unfamiliar names rather than dropping them. Output only the translation, " +
                "without identifying the language or adding explanations. " + MixedTextInstruction(text, targetCode) + literalInstructions + "\n\n\n" +
                TranslationTextNormalizer.Normalize(text).Trim() + "<end_of_turn>\n<start_of_turn>model\n";
        }
        var source = Language(sourceCode);
        // TranslateGemma's trained text template, taken from the installed GGUF
        // metadata. Each segment has its own response; no JSON repair or truncation.
        return $"<bos><start_of_turn>user\nYou are a professional {source.Name} ({source.Code}) to " +
            $"{target.Name} ({target.Code}) translator. Your goal is to accurately convey the meaning and " +
            $"nuances of the original {source.Name} text while adhering to {target.Name} grammar, " +
            $"vocabulary, and cultural sensitivities.\nProduce only the {target.Name} translation, " +
            "without any additional explanations or commentary. " + MixedTextInstruction(text, targetCode) + literalInstructions +
            "Please translate the following " +
            $"{source.Name} text into {target.Name}:\n\n\n{TranslationTextNormalizer.Normalize(text).Trim()}<end_of_turn>\n<start_of_turn>model\n";
    }
}
