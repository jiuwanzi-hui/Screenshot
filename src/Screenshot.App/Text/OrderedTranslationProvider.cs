namespace Screenshot.App.Text;

public sealed class OrderedTranslationProvider : ITranslationProvider
{
    // Smaller batches keep online providers under their latency budget; a
    // full-screen OCR pass still fans out across several batches, and a
    // timed-out batch is halved again before we give up on that provider.
    private const int MaximumSegmentsPerBatch = 24;
    private const int MaximumCharactersPerBatch = 1800;
    // Medium selections use the large-capture scheduler too; the regular
    // path can otherwise wait for a full provider timeout on one OCR block.
    private const int PreferFastProvidersSegmentThreshold = 4;
    // Local OpenAI-compatible servers usually serialize model inference.
    // Eight simultaneous requests make every request wait behind the same
    // model and turn a successful translation into a wall of timeouts.
    private const int MaximumConcurrentOnlineBatches = 4;
    // Keep enough neighboring context for the model while limiting a full
    // screen to a small number of requests. Tiny batches multiply network
    // latency and make a working provider miss the capture deadline.
    private const int LargeCaptureOnlineSegmentsPerBatch = 24;
    private const int LargeCaptureOnlineCharactersPerBatch = 1800;
    private const int LargeCaptureOfflineSegmentsPerBatch = 64;
    private const int LargeCaptureOfflineCharactersPerBatch = 8000;
    // The capture UI must remain interactive. This is a deadline for the
    // whole translation request, not an independent timeout per provider.
    private static readonly TimeSpan TranslationBudget =
        TimeSpan.FromSeconds(9.5);
    // Network models have request and generation latency that the local
    // Bergamot path does not. Keep the local interaction target unchanged,
    // but do not cancel a healthy online response just before it completes.
    private static readonly TimeSpan OnlineTranslationBudget =
        TimeSpan.FromSeconds(24);
    private static readonly TimeSpan DefaultOfflineTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan DefaultOnlineTimeout = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan DefaultLargeModelTimeout = TimeSpan.FromSeconds(30);
    private readonly IReadOnlyList<ITranslationProvider> _providers;
    private readonly TimeSpan _offlineTimeout;
    private readonly TimeSpan _onlineTimeout;
    private readonly TimeSpan _translationBudget;
    private readonly TimeSpan _onlineTranslationBudget;

    public OrderedTranslationProvider(IReadOnlyList<ITranslationProvider> providers)
        : this(providers, DefaultOfflineTimeout, DefaultOnlineTimeout)
    {
    }

    internal OrderedTranslationProvider(
        IReadOnlyList<ITranslationProvider> providers,
        TimeSpan offlineTimeout,
        TimeSpan onlineTimeout)
        : this(
            providers,
            offlineTimeout,
            onlineTimeout,
            TranslationBudget,
            OnlineTranslationBudget)
    {
    }

    internal OrderedTranslationProvider(
        IReadOnlyList<ITranslationProvider> providers,
        TimeSpan offlineTimeout,
        TimeSpan onlineTimeout,
        TimeSpan translationBudget,
        TimeSpan onlineTranslationBudget)
    {
        ArgumentNullException.ThrowIfNull(providers);
        if (providers.Count == 0)
        {
            throw new ArgumentException("至少需要一个翻译提供方。", nameof(providers));
        }

        _providers = providers.ToArray();
        _offlineTimeout = offlineTimeout > TimeSpan.Zero
            ? offlineTimeout
            : throw new ArgumentOutOfRangeException(nameof(offlineTimeout));
        _onlineTimeout = onlineTimeout > TimeSpan.Zero
            ? onlineTimeout
            : throw new ArgumentOutOfRangeException(nameof(onlineTimeout));
        _translationBudget = translationBudget > TimeSpan.Zero
            ? translationBudget
            : throw new ArgumentOutOfRangeException(nameof(translationBudget));
        _onlineTranslationBudget = onlineTranslationBudget > TimeSpan.Zero
            ? onlineTranslationBudget
            : throw new ArgumentOutOfRangeException(nameof(onlineTranslationBudget));
    }

    public string Id => "OrderedFallback";

    public IReadOnlyList<string> ProviderIds =>
        _providers.Select(provider => provider.Id).ToArray();

    public async Task<TranslationResult> TranslateAsync(
        string text,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken = default)
    {
        var errors = new List<string>(_providers.Count);
        foreach (var provider in _providers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var result = await InvokeWithTimeoutAsync(
                    provider,
                    text,
                    sourceLanguage,
                    targetLanguage,
                    cancellationToken);
                if (result.IsSuccess &&
                    HasMeaningfulTranslation(
                        [text],
                        [result.Text],
                        targetLanguage) &&
                    HasPlausibleTargetLanguage(
                        [text],
                        [result.Text],
                        targetLanguage) &&
                    !ContainsUntranslatedHanText(
                        [text],
                        [result.Text],
                        targetLanguage))
                {
                    return result;
                }

                errors.Add(FormatError(
                    provider,
                    result.IsSuccess
                        ? GetInvalidTranslationMessage(
                            [text],
                            [result.Text],
                            targetLanguage)
                        : result.ErrorMessage));
            }
            catch (OperationCanceledException) when (
                cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                errors.Add(FormatError(provider, exception.Message));
            }
        }

        return TranslationResult.Failure(CreateFailureMessage(errors));
    }

    public async Task<TranslationSegmentsResult> TranslateSegmentsAsync(
        IReadOnlyList<string> segments,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken = default)
    {
        var hasOnlineProvider = _providers.Any(IsOnlineProvider);
        var hasLocalProvider = _providers.Any(provider =>
            !IsOnlineProvider(provider));
        var captureBudget =
            (hasLocalProvider ? _translationBudget : TimeSpan.Zero) +
            (hasOnlineProvider ? _onlineTranslationBudget : TimeSpan.Zero);
        using var deadlineCancellation =
            new CancellationTokenSource(captureBudget);
        using var linkedCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                deadlineCancellation.Token);
        try
        {
            return await TranslateSegmentsCoreAsync(
                segments,
                sourceLanguage,
                targetLanguage,
                linkedCancellation.Token,
                deadlineCancellation.Token);
        }
        catch (OperationCanceledException) when (
            deadlineCancellation.IsCancellationRequested &&
            !cancellationToken.IsCancellationRequested)
        {
            return TranslationSegmentsResult.Failure(
                hasOnlineProvider
                    ? "在线整张截图翻译超时，未完成区域保留原文。"
                    : "整张截图翻译达到 10 秒时间限制，未完成区域保留原文。");
        }
    }

    private async Task<TranslationSegmentsResult> TranslateSegmentsCoreAsync(
        IReadOnlyList<string> segments,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken,
        CancellationToken deadlineToken)
    {
        ArgumentNullException.ThrowIfNull(segments);
        if (segments.Count == 0)
        {
            return TranslationSegmentsResult.Failure("没有可翻译的文字。");
        }

        // Large OCR captures stall for minutes when a general-purpose local
        // LLM is probed first. Prefer purpose-built / online providers, and
        // only fall back to Qwen after those have been tried.
        var providers = PreferFastProvidersForLargeCaptures(
            _providers,
            segments.Count);
        if (segments.Count >= PreferFastProvidersSegmentThreshold &&
            providers.Any(provider =>
                string.Equals(
                    provider.Id,
                    TranslationProviderFactory.OfflineProviderId,
                    StringComparison.OrdinalIgnoreCase) ||
                string.Equals(
                    provider.Id,
                    TranslationProviderFactory.OpenAiCompatibleProviderId,
                    StringComparison.OrdinalIgnoreCase)))
        {
            return await TranslateLargeCaptureAsync(
                segments,
                sourceLanguage,
                targetLanguage,
                providers,
                _providers.FirstOrDefault(provider => string.Equals(
                    provider.Id,
                    TranslationProviderFactory.LocalLargeModelProviderId,
                    StringComparison.OrdinalIgnoreCase)),
                cancellationToken,
                deadlineToken);
        }

        if (providers.Count == 0)
        {
            return TranslationSegmentsResult.Failure(
                "当前目标语言的离线模型不可用，请安装对应语言包或配置在线翻译。");
        }

        var batches = CreateBatches(segments);
        // A full-screen capture produces many batches. Without this shared
        // state every batch re-probed every dead provider — with the local
        // large-model timeout, a dozen batches could stall the UI for the
        // better part of an hour and looked like a hard freeze.
        var chain = new ProviderChainState(providers.Count);
        if (batches.Count == 1)
        {
            return await TranslateSegmentBatchAsync(
                batches[0].Segments,
                sourceLanguage,
                targetLanguage,
                providers,
                chain,
                cancellationToken);
        }

        var translated = new string[segments.Count];
        var failures = new List<string>();

        if (providers.Count == 1 && string.Equals(
                providers[0].Id,
                TranslationProviderFactory.OpenAiCompatibleProviderId,
                StringComparison.OrdinalIgnoreCase))
        {
            using var throttle = new SemaphoreSlim(
                MaximumConcurrentOnlineBatches);
            var tasks = batches.Select(async batch =>
            {
                await throttle.WaitAsync(cancellationToken);
                try
                {
                    var result = await TranslateSegmentBatchAsync(
                        batch.Segments,
                        sourceLanguage,
                        targetLanguage,
                        providers,
                        new ProviderChainState(providers.Count),
                        cancellationToken);
                    return (Batch: batch, Result: result);
                }
                finally
                {
                    throttle.Release();
                }
            }).ToArray();
            foreach (var completed in await Task.WhenAll(tasks))
            {
                RecordBatchResult(
                    completed.Batch,
                    completed.Result,
                    translated,
                    failures);
            }

            return CreateCombinedResult(segments, translated, failures);
        }

        // The first batch discovers the working provider through the normal
        // fallback chain; the rest go straight to that winner.
        var firstResult = await TranslateSegmentBatchAsync(
            batches[0].Segments,
            sourceLanguage,
            targetLanguage,
            providers,
            chain,
            cancellationToken);
        RecordBatchResult(batches[0], firstResult, translated, failures);

        var remaining = batches.Skip(1).ToArray();
        if (chain.Winner is { } winner &&
            string.Equals(
                winner.Id,
                TranslationProviderFactory.OpenAiCompatibleProviderId,
                StringComparison.OrdinalIgnoreCase))
        {
            // An HTTP provider handles concurrent requests fine; two at a
            // time is enough parallelism without tipping slow APIs into
            // cascading timeouts on large captures.
            using var throttle = new SemaphoreSlim(2);
            var tasks = remaining.Select(async batch =>
            {
                await throttle.WaitAsync(cancellationToken);
                try
                {
                    var result = await TranslateSegmentBatchAsync(
                        batch.Segments,
                        sourceLanguage,
                        targetLanguage,
                        providers,
                        chain,
                        cancellationToken);
                    return (Batch: batch, Result: result);
                }
                finally
                {
                    throttle.Release();
                }
            }).ToArray();
            foreach (var completed in await Task.WhenAll(tasks))
            {
                RecordBatchResult(
                    completed.Batch,
                    completed.Result,
                    translated,
                    failures);
            }
        }
        else
        {
            // Offline and local-model providers are CPU bound and not
            // necessarily reentrant; keep them sequential.
            foreach (var batch in remaining)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = await TranslateSegmentBatchAsync(
                    batch.Segments,
                    sourceLanguage,
                    targetLanguage,
                    providers,
                    chain,
                    cancellationToken);
                RecordBatchResult(batch, result, translated, failures);
            }
        }

        return CreateCombinedResult(segments, translated, failures);
    }

    private async Task<TranslationSegmentsResult> TranslateLargeCaptureAsync(
        IReadOnlyList<string> segments,
        string sourceLanguage,
        string targetLanguage,
        IReadOnlyList<ITranslationProvider> providers,
        ITranslationProvider? localModelFallback,
        CancellationToken cancellationToken,
        CancellationToken deadlineToken)
    {
        var offline = providers.FirstOrDefault(provider => string.Equals(
                provider.Id,
                TranslationProviderFactory.OfflineProviderId,
                StringComparison.OrdinalIgnoreCase));
        var online = providers.FirstOrDefault(provider => string.Equals(
                provider.Id,
                TranslationProviderFactory.OpenAiCompatibleProviderId,
                StringComparison.OrdinalIgnoreCase));

        var offlineRouteAvailable = offline is OfflineTranslationProvider installedOffline &&
            installedOffline.HasInstalledRoute(
                ResolveLargeCaptureSourceLanguage(segments, sourceLanguage),
                targetLanguage);
        // Keep the exact order selected in Settings. A route that fails or
        // exceeds its share of the capture budget falls through to the next
        // configured route; a lower route is never started ahead of a higher
        // route.
        var routes = providers
            .Where(provider =>
                (ReferenceEquals(provider, offline) && offlineRouteAvailable) ||
                ReferenceEquals(provider, online) ||
                string.Equals(
                    provider.Id,
                    TranslationProviderFactory.LocalLargeModelProviderId,
                    StringComparison.OrdinalIgnoreCase))
            .Distinct()
            .ToArray();
        if (routes.Length == 0)
        {
            return TranslationSegmentsResult.Failure(
                "大截图需要已安装的目标语言包或可用的在线翻译服务。");
        }

        var errors = new List<string>();
        TranslationSegmentsResult? bestPartial = null;
        // The configured order is the priority order. Do not divide the
        // ten-second budget evenly: a slow first route used to leave a valid
        // second route with only half a budget, which made large captures
        // report "online translation cancelled" even when that route had
        // available quota. The shared deadline still caps the whole call;
        // each route may use the remaining time before fallback.
        foreach (var route in routes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var routeBudget = IsOnlineProvider(route)
                ? _onlineTranslationBudget
                : _translationBudget;
            using var routeBudgetCancellation =
                new CancellationTokenSource(routeBudget);
            using var routeCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    routeBudgetCancellation.Token,
                    deadlineToken);
            var routeToken = routeCancellation.Token;
            TranslationSegmentsResult result;
            if (ReferenceEquals(route, offline))
            {
                result = await TranslateLargeCaptureOfflineAsync(
                    route,
                    segments,
                    sourceLanguage,
                    targetLanguage,
                    routeToken,
                    deadlineToken);
            }
            else if (string.Equals(
                         route.Id,
                         TranslationProviderFactory.LocalLargeModelProviderId,
                         StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    result = await TranslateLargeCaptureLocalModelAsync(
                        route,
                        segments,
                        sourceLanguage,
                        targetLanguage,
                        routeToken);
                }
                catch (OperationCanceledException)
                {
                    result = TranslationSegmentsResult.Failure(
                        deadlineToken.IsCancellationRequested
                            ? "翻译达到时间限制，未完成区域保留原文。"
                            : "本机离线翻译已取消。");
                }
            }
            else
            {
                result = await TranslateLargeCaptureOnlineAsync(
                    route,
                    segments,
                    sourceLanguage,
                    targetLanguage,
                    routeToken,
                    deadlineToken);
            }
            if (IsCompleteValidTranslation(
                segments,
                result,
                targetLanguage))
            {
                return result;
            }

            // A full-screen OCR pass commonly contains URLs, product names,
            // short navigation labels, and other invariant fragments. Those
            // rows are intentionally preserved by the provider, so strict
            // per-row script validation must not discard an otherwise
            // complete capture. Small selections still use the strict path.
            if (result.IsSuccess &&
                result.Segments.Count == segments.Count &&
                HasMeaningfulTranslation(
                    segments,
                    result.Segments,
                    targetLanguage))
            {
                return result;
            }

            if (IsUsablePartialTranslation(
                    segments,
                    result,
                    targetLanguage))
            {
                bestPartial = result;
                continue;
            }

            if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
            {
                errors.Add(result.ErrorMessage);
            }

            if (deadlineToken.IsCancellationRequested)
            {
                break;
            }
        }

        if (bestPartial is not null)
        {
            return bestPartial;
        }

        // The CPU Qwen fallback is useful for short prose, but a full-screen
        // OCR pass can keep the process busy for tens of seconds and return
        // an incomplete JSON payload. Large captures must stay interactive;
        // reserve Qwen for the normal small-selection path.
        if (localModelFallback is not null && segments.Count < 8)
        {
            var fallback = await TranslateLargeCaptureLocalModelAsync(
                localModelFallback,
                segments,
                sourceLanguage,
                targetLanguage,
                cancellationToken);
            if (IsCompleteValidTranslation(
                segments,
                fallback,
                targetLanguage))
            {
                return fallback;
            }

            if (!string.IsNullOrWhiteSpace(fallback.ErrorMessage))
            {
                errors.Add(fallback.ErrorMessage);
            }
        }

        return TranslationSegmentsResult.Failure(
            errors.Count == 0
                ? "整张截图翻译未完成，请检查在线服务或安装当前目标语言包。"
                : "整张截图未获得完整译文。" +
                  string.Join("；", errors.Distinct(StringComparer.Ordinal)));
    }

    private async Task<TranslationSegmentsResult> TranslateLargeCaptureLocalModelAsync(
        ITranslationProvider provider,
        IReadOnlyList<string> segments,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken)
    {
        var normalizedSegments = segments
            .Select(NormalizeLargeCaptureSegment)
            .ToArray();
        var batches = CreateBatches(normalizedSegments, 16, 1600);
        var translated = new string[normalizedSegments.Length];
        var failures = new List<string>();
        foreach (var batch in batches)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await InvokeSegmentsWithTimeoutAsync(
                provider,
                batch.Segments,
                sourceLanguage,
                targetLanguage,
                GetTimeout(provider, batch.Segments.Count),
                cancellationToken);
            RecordBatchResult(batch, result, translated, failures);
            if (!result.IsSuccess ||
                result.Segments.Count != batch.Segments.Count)
            {
                // A malformed/timeout Qwen response is a provider-level
                // failure, not a reason to start the same model repeatedly
                // for every remaining batch.
                break;
            }
        }

        var combined = CreateCombinedResult(
            normalizedSegments,
            translated,
            failures);
        var restored = combined.Segments.ToArray();
        for (var index = 0; index < restored.Length; index++)
        {
            restored[index] = RestoreLargeCaptureSegment(
                segments[index],
                normalizedSegments[index],
                restored[index]);
        }

        return new TranslationSegmentsResult(
            combined.IsSuccess,
            restored,
            combined.ErrorMessage);
    }

    private async Task<TranslationSegmentsResult> TranslateLargeCaptureOfflineAsync(
        ITranslationProvider offline,
        IReadOnlyList<string> segments,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken,
        CancellationToken deadlineToken)
    {
        // Bergamot is fast for short batches but becomes disproportionately
        // slow when an entire screen is sent in one native call. Keep the
        // original indexes so every translated line still maps to its OCR
        // rectangle, while giving the engine smaller, predictable workloads.
        var effectiveSourceLanguage = ResolveLargeCaptureSourceLanguage(
            segments,
            sourceLanguage);
        if (offline is OfflineTranslationProvider offlineProvider &&
            !offlineProvider.HasInstalledRoute(
                effectiveSourceLanguage,
                targetLanguage))
        {
            return TranslationSegmentsResult.Failure(
                "当前目标语言的离线模型未安装。");
        }
        var batches = CreateBatches(
            segments,
            LargeCaptureOfflineSegmentsPerBatch,
            LargeCaptureOfflineCharactersPerBatch);
        var translated = new string[segments.Count];
        var failures = new List<string>();
        foreach (var batch in batches)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = await InvokeSegmentsWithTimeoutAsync(
                    offline,
                    batch.Segments,
                    effectiveSourceLanguage,
                    targetLanguage,
                    GetTimeout(offline, batch.Segments.Count),
                    cancellationToken);
                // Do not retry individual OCR rows for a full-screen capture.
                // Bergamot already translates the whole bounded batch in one
                // native call; row retries are what previously pushed an
                // otherwise fast result past the ten-second interaction budget.
                RecordBatchResult(batch, result, translated, failures);
            }
            catch (OperationCanceledException)
            {
                failures.Add(deadlineToken.IsCancellationRequested
                    ? "翻译达到时间限制，未完成区域保留原文。"
                    : "翻译已取消，未完成区域保留原文。");
                break;
            }
        }

        return CreateCombinedResult(segments, translated, failures);
    }

    private async Task<TranslationSegmentsResult> RetryUnchangedOfflineSegmentsAsync(
        ITranslationProvider provider,
        IReadOnlyList<string> source,
        TranslationSegmentsResult result,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken)
    {
        var translated = result.Segments.ToArray();
        var failures = new List<string>();
        var retryCount = 0;
        for (var index = 0; index < source.Count; index++)
        {
            if (TranslationTargetLanguageMatcher.IsAlreadyTargetLanguage(
                    source[index], targetLanguage) ||
                HasMeaningfulTranslation(
                    [source[index]], [translated[index]], targetLanguage))
            {
                continue;
            }

            // Do not turn technical identifiers, URLs, or isolated labels
            // into a second full model pass. Retry only natural-language rows,
            // and cap retries per batch so a bad model response remains cheap.
            if (retryCount >= 2 || !IsNaturalLanguageRetryCandidate(source[index]))
            {
                continue;
            }
            retryCount++;

            var retry = await InvokeSegmentsWithTimeoutAsync(
                provider,
                [source[index]],
                sourceLanguage,
                targetLanguage,
                GetTimeout(provider, 1),
                cancellationToken);
            if (retry.IsSuccess && retry.Segments.Count == 1 &&
                HasMeaningfulTranslation(
                    [source[index]], retry.Segments, targetLanguage))
            {
                translated[index] = retry.Segments[0];
            }
            else
            {
                failures.Add($"第 {index + 1} 行：" +
                    (retry.ErrorMessage ?? "译文与原文相同"));
            }
        }

        return CreateCombinedResult(source, translated, failures);
    }

    private static bool IsNaturalLanguageRetryCandidate(string text)
    {
        var value = text.Trim();
        if (value.Length < 8 ||
            value.Contains("http", StringComparison.OrdinalIgnoreCase) ||
            value.Contains('\\') || value.Contains('/') ||
            value.Contains("://", StringComparison.Ordinal))
        {
            return false;
        }

        var words = value.Split(
            (char[]?)null,
            StringSplitOptions.RemoveEmptyEntries);
        return words.Length >= 2 &&
               value.Any(char.IsLetter) &&
               !value.All(character => char.IsUpper(character) ||
                                      !char.IsLetter(character));
    }

    private static string ResolveLargeCaptureSourceLanguage(
        IReadOnlyList<string> segments,
        string sourceLanguage)
    {
        if (TranslationLanguageCatalog.NormalizeOfflineCode(sourceLanguage)
                is not null)
        {
            return sourceLanguage;
        }

        var combinedText = string.Join(
            Environment.NewLine,
            segments.Where(segment => !string.IsNullOrWhiteSpace(segment)));
        var detection = Cld3OfflineLanguageDetector.Shared.Detect(combinedText);
        if (detection.IsSuccess &&
               detection.ErrorMessage is null &&
               detection.LanguageCode is { } detected &&
               TranslationLanguageCatalog.IsSupportedSource(detected)
            )
        {
            // Full-page OCR often contains a small amount of Asian-language
            // chrome mixed with a much larger English article. CLD3 can then
            // classify the concatenated text as Chinese/Japanese, causing an
            // English screenshot to take the wrong (or same-language) route.
            // Prefer English when there is clear dominant English prose.
            var englishProseCharacters = segments
                .Where(TranslationTargetLanguageMatcher.HasLatinNaturalLanguageClause)
                .Sum(segment => segment.Count(char.IsLetter));
            var nonLatinProseCharacters = segments.Sum(segment =>
                segment.Count(character =>
                    character is >= '\u3400' and <= '\u4dbf' or
                    >= '\u4e00' and <= '\u9fff' or
                    >= '\u3040' and <= '\u30ff' or
                    >= '\uac00' and <= '\ud7af'));
            if (englishProseCharacters >= 40 &&
                englishProseCharacters >= nonLatinProseCharacters * 1.2)
            {
                return "en";
            }

            return detected;
        }

        return sourceLanguage;
    }

    private async Task<TranslationSegmentsResult> TranslateLargeCaptureOnlineAsync(
        ITranslationProvider online,
        IReadOnlyList<string> segments,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken,
        CancellationToken deadlineToken)
    {
        var normalizedSegments = segments
            .Select(NormalizeLargeCaptureSegment)
            .ToArray();
        var batches = CreateBatches(
            normalizedSegments,
            LargeCaptureOnlineSegmentsPerBatch,
            LargeCaptureOnlineCharactersPerBatch);
        var translated = new string[normalizedSegments.Length];
        var failures = new List<string>();
        using var throttle = new SemaphoreSlim(MaximumConcurrentOnlineBatches);
        using var providerCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var providerToken = providerCancellation.Token;
        var providerFailureLock = new object();
        string? providerFailureMessage = null;
        var tasks = batches.Select(async batch =>
        {
            var entered = false;
            try
            {
                await throttle.WaitAsync(providerToken);
                entered = true;
                var result = await InvokeSegmentsWithSplitRetriesAsync(
                    online,
                    batch.Segments,
                    sourceLanguage,
                    targetLanguage,
                    providerToken);
                if (!result.IsSuccess &&
                    IsProviderConfigurationFailure(result.ErrorMessage))
                {
                    lock (providerFailureLock)
                    {
                        providerFailureMessage ??= result.ErrorMessage;
                    }

                    // Billing/authentication failures are deterministic. Do
                    // not spend requests on every remaining batch.
                    providerCancellation.Cancel();
                }
                return (Batch: batch, Result: result);
            }
            catch (OperationCanceledException)
            {
                string? configurationFailure;
                lock (providerFailureLock)
                {
                    configurationFailure = providerFailureMessage;
                }
                return (Batch: batch, Result:
                    TranslationSegmentsResult.Failure(
                        deadlineToken.IsCancellationRequested
                            ? "翻译达到时间限制，未完成区域保留原文。"
                            : configurationFailure is not null
                                ? "在线翻译已停止。"
                            : "在线翻译已取消。"));
            }
            finally
            {
                if (entered)
                {
                    throttle.Release();
                }
            }
        }).ToArray();

        foreach (var completed in await Task.WhenAll(tasks))
        {
            string? configurationFailure;
            lock (providerFailureLock)
            {
                configurationFailure = providerFailureMessage;
            }
            if (configurationFailure is not null &&
                !completed.Result.IsSuccess)
            {
                // Report the billing/authentication problem once below,
                // rather than once for every request that was cancelled.
                continue;
            }
            RecordBatchResult(
                completed.Batch,
                completed.Result,
                translated,
                failures);
        }

        lock (providerFailureLock)
        {
            if (!string.IsNullOrWhiteSpace(providerFailureMessage))
            {
                failures.Add(providerFailureMessage);
            }
        }

        var combined = CreateCombinedResult(
            normalizedSegments,
            translated,
            failures);
        if (!combined.IsSuccess)
        {
            return combined;
        }

        // Symbol-only OCR rows do not need a model request. Restore all
        // decorative boundaries in place so bullets and markers remain
        // aligned with the original capture.
        var restored = combined.Segments.ToArray();
        for (var index = 0; index < restored.Length; index++)
        {
            restored[index] = RestoreLargeCaptureSegment(
                segments[index],
                normalizedSegments[index],
                restored[index]);
        }

        return new TranslationSegmentsResult(
            true,
            restored,
            combined.ErrorMessage);
    }

    private static string RestoreLargeCaptureSegment(
        string original,
        string normalized,
        string translated)
    {
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return original;
        }

        var value = original.Trim();
        var leadingLength = 0;
        while (leadingLength < value.Length &&
               IsDecorativeBoundary(value[leadingLength]))
        {
            leadingLength++;
        }

        var trailingLength = 0;
        while (trailingLength < value.Length - leadingLength &&
               IsDecorativeBoundary(
                   value[value.Length - trailingLength - 1]))
        {
            trailingLength++;
        }

        var leading = value[..leadingLength];
        var trailing = trailingLength == 0
            ? string.Empty
            : value[^trailingLength..];
        return leading + translated.Trim() + trailing;
    }

    private static string NormalizeLargeCaptureSegment(string? segment)
    {
        var value = segment?.Trim() ?? string.Empty;
        if (value.Length == 0)
        {
            return string.Empty;
        }

        var compact = string.Join(
            ' ',
            value.Split(
                (char[]?)null,
                StringSplitOptions.RemoveEmptyEntries));
        var start = 0;
        var end = compact.Length - 1;
        while (start <= end && IsDecorativeBoundary(compact[start]))
        {
            start++;
        }

        while (end >= start && IsDecorativeBoundary(compact[end]))
        {
            end--;
        }

        if (start > end)
        {
            return string.Empty;
        }

        return compact[start..(end + 1)];
    }

    private static bool IsDecorativeBoundary(char value)
    {
        var category = char.GetUnicodeCategory(value);
        return category is
            System.Globalization.UnicodeCategory.MathSymbol or
            System.Globalization.UnicodeCategory.CurrencySymbol or
            System.Globalization.UnicodeCategory.ModifierSymbol or
            System.Globalization.UnicodeCategory.OtherSymbol or
            System.Globalization.UnicodeCategory.Format or
            System.Globalization.UnicodeCategory.Control;
    }

    private static bool IsCompleteValidTranslation(
        IReadOnlyList<string> source,
        TranslationSegmentsResult result,
        string targetLanguage)
    {
        return result.IsSuccess &&
            string.IsNullOrWhiteSpace(result.ErrorMessage) &&
            result.Segments.Count == source.Count &&
            HasMeaningfulTranslation(source, result.Segments, targetLanguage) &&
            HasPlausibleTargetLanguage(source, result.Segments, targetLanguage) &&
            !ContainsUntranslatedHanText(source, result.Segments, targetLanguage);
    }

    private static bool IsUsablePartialTranslation(
        IReadOnlyList<string> source,
        TranslationSegmentsResult result,
        string targetLanguage)
    {
        if (!result.IsSuccess ||
            result.Segments.Count != source.Count ||
            string.IsNullOrWhiteSpace(result.ErrorMessage))
        {
            return false;
        }

        return source.Select((text, index) => (text, index))
            .Any(item => !TranslationTargetLanguageMatcher
                .IsAlreadyTargetLanguage(item.text, targetLanguage) &&
                !AreEquivalent(item.text, result.Segments[item.index]));
    }

    private static TranslationSegmentsResult CreateCombinedResult(
        IReadOnlyList<string> source,
        string[] translated,
        IReadOnlyList<string> failures)
    {
        if (failures.Count == 0)
        {
            return new TranslationSegmentsResult(true, translated, null);
        }

        for (var index = 0; index < translated.Length; index++)
        {
            translated[index] ??= source[index];
        }

        return new TranslationSegmentsResult(
            true,
            translated,
            "部分行翻译失败，已保留原文。" + string.Join("；", failures));
    }

    private static void RecordBatchResult(
        TranslationBatch batch,
        TranslationSegmentsResult result,
        string[] translated,
        List<string> failures)
    {
        if (!result.IsSuccess || result.Segments.Count != batch.Segments.Count)
        {
            lock (failures)
            {
                failures.Add(
                    $"第 {batch.StartIndex + 1}-{batch.StartIndex + batch.Segments.Count} 行：" +
                    (result.ErrorMessage ?? "翻译结果不完整"));
            }

            return;
        }

        for (var index = 0; index < result.Segments.Count; index++)
        {
            translated[batch.StartIndex + index] = result.Segments[index];
        }
    }

    private async Task<TranslationSegmentsResult> TranslateSegmentBatchAsync(
        IReadOnlyList<string> segments,
        string sourceLanguage,
        string targetLanguage,
        IReadOnlyList<ITranslationProvider> providers,
        ProviderChainState chain,
        CancellationToken cancellationToken)
    {
        var errors = new List<string>(providers.Count);
        for (var index = 0; index < providers.Count; index++)
        {
            // Try the established winner first, then the rest in order,
            // skipping providers that already failed during this call: a
            // provider that timed out or errored once will do it again for
            // every batch of the same request, and re-probing it turned
            // large captures into multi-minute stalls.
            var provider = chain.SelectProvider(index, providers);
            if (provider is null)
            {
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var result = await InvokeSegmentsWithSplitRetriesAsync(
                    provider,
                    segments,
                    sourceLanguage,
                    targetLanguage,
                    cancellationToken);
                if (result.IsSuccess &&
                    HasMeaningfulTranslation(
                        segments,
                        result.Segments,
                        targetLanguage) &&
                    HasPlausibleTargetLanguage(
                        segments,
                        result.Segments,
                        targetLanguage) &&
                    !ContainsUntranslatedHanText(
                        segments,
                        result.Segments,
                        targetLanguage))
                {
                    chain.MarkSucceeded(provider);
                    return result;
                }

                var failureMessage = result.IsSuccess
                    ? GetInvalidTranslationMessage(
                        segments,
                        result.Segments,
                        targetLanguage)
                    : result.ErrorMessage;
                // A single oversized-batch timeout is not proof the provider
                // is dead — split retries already ran above. Only hard-fail
                // after those retries are exhausted, so later batches still
                // skip a truly stuck provider without re-probing forever.
                chain.MarkFailed(provider, providers);
                errors.Add(FormatError(provider, failureMessage));
            }
            catch (OperationCanceledException) when (
                cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                chain.MarkFailed(provider, providers);
                errors.Add(FormatError(provider, exception.Message));
            }
        }

        return TranslationSegmentsResult.Failure(
            errors.Count == 0
                ? "所有翻译方式此前均已失败。"
                : CreateFailureMessage(errors));
    }

    /// <summary>
    /// When a provider times out on a large batch, halve the batch and retry
    /// before falling through to the next provider. Soft timeouts used to
    /// blacklist the online provider after the first 40-line attempt, which
    /// left every later batch with "此前均已失败" and no translation at all.
    /// </summary>
    private async Task<TranslationSegmentsResult> InvokeSegmentsWithSplitRetriesAsync(
        ITranslationProvider provider,
        IReadOnlyList<string> segments,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken,
        int splitDepth = 0)
    {
        var timeout = GetTimeout(provider, segments.Count);
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var canSplit = string.Equals(
            provider.Id,
            TranslationProviderFactory.OpenAiCompatibleProviderId,
            StringComparison.OrdinalIgnoreCase) &&
            segments.Count > 1;
        var firstAttemptTimeout = canSplit
            ? TimeSpan.FromTicks(timeout.Ticks * 2 / 3)
            : timeout;
        var result = await InvokeSegmentsWithTimeoutAsync(
            provider,
            segments,
            sourceLanguage,
            targetLanguage,
            firstAttemptTimeout,
            cancellationToken);

        // Some chat models return a valid JSON envelope but omit lines from a
        // large request. Retry that same provider with smaller concurrent
        // requests before blacklisting it for the rest of the capture.
        if (!result.IsSuccess &&
            canSplit &&
            segments.Count > 1 &&
            splitDepth < 3 &&
            IsSplittableBatchFailure(result.ErrorMessage))
        {
            var splitBudget = timeout - timer.Elapsed;
            if (splitBudget > TimeSpan.Zero)
            {
                var splitIndex = segments.Count / 2;
                var splitLeftTask = InvokeSegmentsWithSplitRetriesAsync(
                    provider,
                    segments.Take(splitIndex).ToArray(),
                    sourceLanguage,
                    targetLanguage,
                    cancellationToken,
                    splitDepth + 1);
                var splitRightTask = InvokeSegmentsWithSplitRetriesAsync(
                    provider,
                    segments.Skip(splitIndex).ToArray(),
                    sourceLanguage,
                    targetLanguage,
                    cancellationToken,
                    splitDepth + 1);
                await Task.WhenAll(splitLeftTask, splitRightTask);
                var splitLeft = await splitLeftTask;
                var splitRight = await splitRightTask;
                if (splitLeft.IsSuccess && splitRight.IsSuccess &&
                    splitLeft.Segments.Count == splitIndex &&
                    splitRight.Segments.Count == segments.Count - splitIndex)
                {
                    return new TranslationSegmentsResult(
                        true,
                        splitLeft.Segments.Concat(splitRight.Segments).ToArray(),
                        null);
                }
            }
        }

        if (result.IsSuccess ||
            !IsTransientTimeout(result.ErrorMessage) ||
            !canSplit)
        {
            return result;
        }

        var mid = segments.Count / 2;
        // A timed-out large request has already consumed its budget. Giving
        // each smaller retry a fresh provider timeout is intentional: a model
        // that needs several seconds for eight lines can still finish two
        // smaller requests instead of receiving only the time left over from
        // the failed parent request.
        var leftTask = InvokeSegmentsWithTimeoutAsync(
            provider,
            segments.Take(mid).ToArray(),
            sourceLanguage,
            targetLanguage,
            timeout,
            cancellationToken);
        var rightTask = InvokeSegmentsWithTimeoutAsync(
            provider,
            segments.Skip(mid).ToArray(),
            sourceLanguage,
            targetLanguage,
            timeout,
            cancellationToken);
        await Task.WhenAll(leftTask, rightTask);
        var left = await leftTask;
        var right = await rightTask;
        if (!left.IsSuccess)
        {
            return left;
        }

        if (!right.IsSuccess)
        {
            return right;
        }

        var merged = new string[left.Segments.Count + right.Segments.Count];
        for (var index = 0; index < left.Segments.Count; index++)
        {
            merged[index] = left.Segments[index];
        }

        for (var index = 0; index < right.Segments.Count; index++)
        {
            merged[left.Segments.Count + index] = right.Segments[index];
        }

        return new TranslationSegmentsResult(true, merged, null);
    }

    private static bool IsSplittableBatchFailure(string? errorMessage)
    {
        return !string.IsNullOrWhiteSpace(errorMessage) &&
            (errorMessage.Contains("分段结果不完整", StringComparison.Ordinal) ||
             errorMessage.Contains("分段格式", StringComparison.Ordinal) ||
             errorMessage.Contains("原样返回", StringComparison.Ordinal));
    }

    private static bool IsTransientTimeout(string? errorMessage)
    {
        return !string.IsNullOrWhiteSpace(errorMessage) &&
               errorMessage.Contains("超时", StringComparison.Ordinal);
    }

    private static bool IsOnlineProvider(ITranslationProvider provider)
    {
        return string.Equals(
            provider.Id,
            TranslationProviderFactory.OpenAiCompatibleProviderId,
            StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<ITranslationProvider> PreferFastProvidersForLargeCaptures(
        IReadOnlyList<ITranslationProvider> providers,
        int segmentCount)
    {
        // The order in Settings is the contract. Performance tuning may
        // change batching and concurrency, but it must never silently move
        // an online provider ahead of the user's selected offline model (or
        // skip that model for a large capture).
        return providers;
    }

    private static bool IsProviderConfigurationFailure(string? errorMessage)
    {
        return !string.IsNullOrWhiteSpace(errorMessage) &&
               (errorMessage.Contains("余额不足", StringComparison.Ordinal) ||
                errorMessage.Contains("HTTP 402", StringComparison.Ordinal) ||
                errorMessage.Contains("HTTP 401", StringComparison.Ordinal) ||
                errorMessage.Contains("HTTP 403", StringComparison.Ordinal) ||
                errorMessage.Contains("拒绝了凭据", StringComparison.Ordinal));
    }

    /// <summary>
    /// Shared fallback state for one multi-batch translation call. Thread
    /// safe because online batches run concurrently.
    /// </summary>
    private sealed class ProviderChainState
    {
        private readonly bool[] _failed;
        private volatile ITranslationProvider? _winner;

        public ProviderChainState(int providerCount)
        {
            _failed = new bool[providerCount];
        }

        public ITranslationProvider? Winner => _winner;

        public ITranslationProvider? SelectProvider(
            int position,
            IReadOnlyList<ITranslationProvider> providers)
        {
            var winner = _winner;
            if (position == 0 && winner is not null)
            {
                return winner;
            }

            var provider = providers[position];
            if (ReferenceEquals(provider, winner))
            {
                // Already tried first.
                return null;
            }

            lock (_failed)
            {
                return _failed[position] ? null : provider;
            }
        }

        public void MarkSucceeded(ITranslationProvider provider)
        {
            _winner = provider;
        }

        public void MarkFailed(
            ITranslationProvider provider,
            IReadOnlyList<ITranslationProvider> providers)
        {
            if (ReferenceEquals(_winner, provider))
            {
                _winner = null;
            }

            for (var index = 0; index < providers.Count; index++)
            {
                if (ReferenceEquals(providers[index], provider))
                {
                    lock (_failed)
                    {
                        _failed[index] = true;
                    }

                    return;
                }
            }
        }
    }

    private static List<TranslationBatch> CreateBatches(
        IReadOnlyList<string> segments,
        int maximumSegments = MaximumSegmentsPerBatch,
        int maximumCharacters = MaximumCharactersPerBatch)
    {
        var batches = new List<TranslationBatch>();
        var current = new List<string>();
        var currentCharacters = 0;
        var startIndex = 0;

        foreach (var segment in segments)
        {
            var segmentLength = segment?.Length ?? 0;
            if (current.Count > 0 &&
                (current.Count >= maximumSegments ||
                 currentCharacters + segmentLength > maximumCharacters))
            {
                batches.Add(new TranslationBatch(startIndex, current.ToArray()));
                startIndex += current.Count;
                current.Clear();
                currentCharacters = 0;
            }

            current.Add(segment ?? string.Empty);
            currentCharacters += segmentLength;
        }

        if (current.Count > 0)
        {
            batches.Add(new TranslationBatch(startIndex, current.ToArray()));
        }

        return batches;
    }

    private static string FormatError(
        ITranslationProvider provider,
        string? errorMessage)
    {
        var label = provider.Id switch
        {
            TranslationProviderFactory.OpenAiCompatibleProviderId => "在线大模型",
            TranslationProviderFactory.OfflineProviderId => "离线模型",
            TranslationProviderFactory.LocalLargeModelProviderId =>
                "Qwen 本机大模型",
            _ => provider.Id,
        };
        return $"{label}：{(string.IsNullOrWhiteSpace(errorMessage) ? "不可用" : errorMessage)}";
    }

    private static string CreateFailureMessage(IReadOnlyList<string> errors)
    {
        return "所有翻译方式均不可用。" + string.Join("；", errors);
    }

    private async Task<TranslationResult> InvokeWithTimeoutAsync(
        ITranslationProvider provider,
        string text,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken)
    {
        using var providerCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        var providerTask = Task.Run(
            () => provider.TranslateAsync(
                text,
                sourceLanguage,
                targetLanguage,
                providerCancellation.Token),
            CancellationToken.None);
        try
        {
            return await providerTask.WaitAsync(
                    GetTimeout(provider, segmentCount: 1),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            providerCancellation.Cancel();
            return TranslationResult.Failure("翻译超时，已切换到下一种翻译方式");
        }
    }

    private static async Task<TranslationSegmentsResult> InvokeSegmentsWithTimeoutAsync(
        ITranslationProvider provider,
        IReadOnlyList<string> segments,
        string sourceLanguage,
        string targetLanguage,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var providerCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        var providerTask = Task.Run(
            () => provider.TranslateSegmentsAsync(
                segments,
                sourceLanguage,
                targetLanguage,
                providerCancellation.Token),
            CancellationToken.None);
        try
        {
            return await providerTask.WaitAsync(
                    timeout,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            providerCancellation.Cancel();
            return TranslationSegmentsResult.Failure(
                "翻译超时，已切换到下一种翻译方式");
        }
    }

    private TimeSpan GetTimeout(ITranslationProvider provider, int segmentCount)
    {
        var count = Math.Max(1, segmentCount);
        if (string.Equals(
                provider.Id,
                TranslationProviderFactory.LocalLargeModelProviderId,
                StringComparison.OrdinalIgnoreCase))
        {
            // Qwen is a last resort for large OCR passes; keep the probe short
            // enough that a bad run does not freeze the UI for minutes.
            return TimeSpan.FromSeconds(Math.Min(
                DefaultLargeModelTimeout.TotalSeconds,
                18 + count));
        }

        if (string.Equals(
                provider.Id,
                TranslationProviderFactory.OfflineProviderId,
                StringComparison.OrdinalIgnoreCase))
        {
            // A stuck native batch must not consume the entire interaction
            // budget. Smaller batches and bounded retries preserve partial
            // output while keeping a normal capture responsive.
            var seconds = segmentCount >= 4 ? 10 : 6;
            return TimeSpan.FromSeconds(
                Math.Min(_offlineTimeout.TotalSeconds, seconds));
        }

        // Online latency grows with prompt size; give large batches room to
        // finish before we split and retry.
        return _onlineTimeout;
    }

    internal static bool ContainsUntranslatedHanText(
        IReadOnlyList<string> sourceSegments,
        IReadOnlyList<string> translatedSegments,
        string targetLanguage)
    {
        if (!string.Equals(
                TranslationLanguageCatalog.NormalizeOfflineCode(targetLanguage),
                "en",
                StringComparison.OrdinalIgnoreCase) ||
            !sourceSegments.Any(segment => segment.Count(IsHanCharacter) >= 2))
        {
            return false;
        }

        return translatedSegments.Any(segment => segment.Count(IsHanCharacter) >= 2);
    }

    internal static bool HasMeaningfulTranslation(
        IReadOnlyList<string> sourceSegments,
        IReadOnlyList<string> translatedSegments,
        string targetLanguage)
    {
        var needsTranslation = sourceSegments
            .Select((segment, index) => (Segment: segment, Index: index))
            .Where(item => !TranslationTargetLanguageMatcher.IsAlreadyTargetLanguage(
                item.Segment,
                targetLanguage))
            .ToArray();

        if (needsTranslation.Length == 0)
        {
            return true;
        }

        var hasSubstantiveChange = needsTranslation.Any(item =>
            item.Index < translatedSegments.Count &&
            !AreEquivalent(item.Segment, translatedSegments[item.Index]) &&
            !TranslationTargetLanguageMatcher.IsAmbiguousShortLabel(
                item.Segment));
        return needsTranslation.All(item =>
            item.Index < translatedSegments.Count &&
            (!AreEquivalent(item.Segment, translatedSegments[item.Index]) ||
             TranslationTargetLanguageMatcher.IsLikelyInvariant(item.Segment) ||
             hasSubstantiveChange &&
             TranslationTargetLanguageMatcher.IsAmbiguousShortLabel(
                 item.Segment)));
    }

    internal static bool HasPlausibleTargetLanguage(
        IReadOnlyList<string> sourceSegments,
        IReadOnlyList<string> translatedSegments,
        string targetLanguage)
    {
        var target = TranslationLanguageCatalog.NormalizeOfflineCode(
            targetLanguage);
        var hasSubstantiveChange = sourceSegments
            .Select((segment, index) => (Segment: segment, Index: index))
            .Any(item => item.Index < translatedSegments.Count &&
                !AreEquivalent(
                    item.Segment,
                    translatedSegments[item.Index]) &&
                !TranslationTargetLanguageMatcher.IsAmbiguousShortLabel(
                    item.Segment));
        for (var index = 0; index < sourceSegments.Count; index++)
        {
            if (index >= translatedSegments.Count)
            {
                return false;
            }

            var source = sourceSegments[index];
            var translated = translatedSegments[index]?.Trim() ?? string.Empty;
            if (AreEquivalent(source, translated) &&
                (TranslationTargetLanguageMatcher.IsAlreadyTargetLanguage(
                     source,
                     targetLanguage) ||
                 TranslationTargetLanguageMatcher.IsLikelyInvariant(source) ||
                 hasSubstantiveChange &&
                 TranslationTargetLanguageMatcher.IsAmbiguousShortLabel(source)))
            {
                continue;
            }
            if (translated.Length == 0 ||
                IsOpaqueHexOutput(source, translated) ||
                (target is "ja" or "ko" or "ru" or "bg" or "uk" or "be" or
                    "sr" or "ar" or "fa" or "ur" &&
                 IsSuspiciouslyCompressed(source, translated)) ||
                !HasExpectedTargetScript(source, translated, target))
            {
                return false;
            }

            if (target == "en" && source.Count(IsHanCharacter) >= 2)
            {
                var letters = translated.Count(char.IsLetter);
                var latin = translated.Count(character =>
                    character is >= 'A' and <= 'Z' or >= 'a' and <= 'z');
                if (translated.Count(IsHanCharacter) >= 2 ||
                    letters == 0 || latin < letters * 0.7)
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static bool IsSuspiciouslyCompressed(
        string source,
        string translated)
    {
        var sourceContent = source.Count(char.IsLetterOrDigit);
        var translatedContent = translated.Count(char.IsLetterOrDigit);
        return sourceContent >= 16 && translatedContent < sourceContent * 0.35;
    }

    private static bool HasExpectedTargetScript(
        string source,
        string translated,
        string? target)
    {
        if (source.Count(char.IsLetter) < 8)
        {
            return true;
        }

        return target switch
        {
            "ja" => translated.Any(character =>
                character is >= '\u3040' and <= '\u30ff'),
            "ko" => translated.Any(character =>
                character is >= '\uac00' and <= '\ud7af'),
            "ru" or "bg" or "uk" or "be" or "sr" =>
                translated.Any(character =>
                    character is >= '\u0400' and <= '\u04ff'),
            "ar" or "fa" or "ur" => translated.Any(character =>
                character is >= '\u0600' and <= '\u06ff'),
            "zh" or "zh_hant" => translated.Any(IsHanCharacter),
            _ => true,
        };
    }

    private static bool IsOpaqueHexOutput(string source, string translated)
    {
        if (translated.Length < 16 ||
            translated.Any(char.IsWhiteSpace) ||
            source.All(character => character <= 0x7f))
        {
            return false;
        }

        var hexCharacters = translated.Count(Uri.IsHexDigit);
        return hexCharacters >= translated.Length * 0.9;
    }

    private static bool IsLikelyInvariant(string text)
    {
        var value = text.Trim();
        if (value.Length <= 1 || !value.Any(char.IsLetter))
        {
            return true;
        }

        if (Uri.TryCreate(value, UriKind.Absolute, out _) ||
            value.Contains('@') ||
            value.Any(character => character is '\\' or '/' or '_'))
        {
            return true;
        }

        var pathToken = value.Split(
            [' ', '\t'],
            StringSplitOptions.RemoveEmptyEntries)[0];
        if (!string.IsNullOrWhiteSpace(System.IO.Path.GetExtension(pathToken)) ||
            LooksLikeCodeIdentifier(value))
        {
            return true;
        }

        var words = value.Split(
            [' ', '\t', '-', '.', ':'],
            StringSplitOptions.RemoveEmptyEntries);
        return words.Any(word =>
            word.Length >= 2 &&
            word.Any(char.IsLetter) &&
            word.Where(char.IsLetter).All(char.IsUpper));
    }

    private static bool LooksLikeCodeIdentifier(string value)
    {
        if (value.Any(char.IsWhiteSpace))
        {
            return false;
        }

        return value.Contains('.') || value.Contains("::", StringComparison.Ordinal) ||
            value.Count(character => character is >= 'A' and <= 'Z') >= 2;
    }

    private static string GetInvalidTranslationMessage(
        IReadOnlyList<string> sourceSegments,
        IReadOnlyList<string> translatedSegments,
        string targetLanguage)
    {
        if (!HasPlausibleTargetLanguage(
                sourceSegments,
                translatedSegments,
                targetLanguage))
        {
            return "译文不是有效的目标语言内容";
        }

        return ContainsUntranslatedHanText(
            sourceSegments,
            translatedSegments,
            targetLanguage)
            ? "译文仍包含未翻译的中文"
            : "未产生有效译文";
    }

    private static bool AreEquivalent(string source, string translated)
    {
        static string Normalize(string value) => string.Concat(
            value.Where(char.IsLetterOrDigit)
                .Select(char.ToUpperInvariant));

        return string.Equals(
            Normalize(source),
            Normalize(translated),
            StringComparison.Ordinal);
    }

    private static bool IsHanCharacter(char value)
    {
        return value is >= '\u3400' and <= '\u4DBF' or
            >= '\u4E00' and <= '\u9FFF' or
            >= '\uF900' and <= '\uFAFF';
    }

    private sealed record TranslationBatch(
        int StartIndex,
        IReadOnlyList<string> Segments);
}
