using System.Diagnostics;
using System.Drawing;
using Screenshot.App.Capture;
using Screenshot.App.Core;

namespace Screenshot.App.Text.Realtime;

internal sealed record RealtimeTranslationBlock(OcrTextRegion Region, string Translation)
{
    internal RealtimeTextAppearance? Appearance { get; init; }
}
internal sealed record RealtimeTranslationUpdate(ScreenRegion Bounds, int PixelWidth, int PixelHeight,
    IReadOnlyList<RealtimeTranslationBlock> Blocks, string Status)
{
    internal bool HasFailures { get; init; }
    internal bool IsReadingSnapshot { get; init; }
    internal IReadOnlyList<RealtimeTranslationBlock> ReadingBlocks { get; init; } = [];
}

internal sealed class RealtimeTranslationSession : IAsyncDisposable
{
    private sealed record Work(long Version, ScreenRegion Bounds, int Width, int Height,
        IReadOnlyList<OcrTextRegion> Regions, IReadOnlyList<RealtimeTextAppearance> Appearance);
    private sealed record Anchors(ScreenRegion Bounds, Rectangle[] Areas, RealtimeImageProbe[] Probes);
    private sealed record Scene(ScreenRegion Bounds, RealtimeImageProbe Probe);
    private readonly IRealtimeCaptureSource _source;
    private readonly Func<CapturedImage, CancellationToken, Task<OcrRecognitionResult>> _recognize;
    private readonly ITranslationProvider _provider;
    private readonly string _target;
    private readonly RealtimeGlossary _glossary;
    private readonly Func<bool> _canCapture;
    private readonly TimeProvider _clock;
    private readonly CancellationTokenSource _stop = new();
    private readonly LatestFrameMailbox _frames = new();
    private readonly SemaphoreSlim _textReady = new(0, 1);
    private readonly object _gate = new();
    private readonly Dictionary<string, string> _cache = new(StringComparer.Ordinal);
    private readonly Queue<string> _cacheOrder = new();
    private CancellationTokenSource? _translationCancel;
    private CancellationTokenSource? _recognitionCancel;
    private Work? _pending;
    private Work? _current;
    private Anchors? _anchors;
    private Scene? _latestScene;
    private RealtimeTranslationUpdate? _lastOutput;
    private RealtimeTranslationUpdate? _presentedOutput;
    private RealtimeTranslationUpdate? _readingOutput;
    private readonly List<(OcrTextRegion Region, long Changed)> _movingRegions = [];
    private bool _awaitingStableScene;
    private long _lastSceneChange;
    private long _readingSince;
    private long _lastMotion;
    private int _motionFrames;
    private bool _continuousMotion;
    internal const string ReadingStatus = "画面变化中，保留上一画面译文";
    private static readonly TimeSpan SettleTime = TimeSpan.FromMilliseconds(350);
    private static readonly TimeSpan MinimumReadingTime = TimeSpan.FromMilliseconds(1500);
    private Task? _completion;
    private long _version;
    private bool _paused;
    private bool _hidden = true;
    private bool _visualValid = true;
    private bool _disposed;
    private bool _available = true;
    private int _sampleMilliseconds = 160;
    internal event Action<RealtimeTranslationUpdate>? Updated;
    internal event Action? Invalidated;
    internal event Action<string>? StatusChanged;
    internal Task Completion => _completion ?? Task.CompletedTask;

    internal RealtimeTranslationSession(IRealtimeCaptureSource source,
        Func<CapturedImage, CancellationToken, Task<OcrRecognitionResult>> recognize,
        ITranslationProvider provider, string target, RealtimeGlossary glossary, Func<bool>? canCapture = null,
        TimeProvider? clock = null)
    {
        _source = source; _recognize = recognize; _provider = provider;
        _target = target; _glossary = glossary; _canCapture = canCapture ?? (() => true);
        _clock = clock ?? TimeProvider.System;
    }

    internal void Start() => _completion ??= Task.Run(RunAsync);

    internal void Retry()
    {
        lock (_gate)
        {
            if (_paused || !_available || _current is null || _disposed || Completion.IsCompleted) return;
            _translationCancel?.Cancel();
            _current = _current with { Version = ++_version };
            _pending = _current;
            if (_textReady.CurrentCount == 0) _textReady.Release();
        }
    }

    internal void SetPaused(bool paused)
    {
        lock (_gate)
        {
            _paused = paused;
            _version++;
            _current = null; _pending = null; _anchors = null; _latestScene = null; _lastOutput = null;
            _translationCancel?.Cancel();
            _recognitionCancel?.Cancel();
            HideLocked();
        }
        StatusChanged?.Invoke(paused ? "已暂停；继续后识别最新画面" : "正在恢复实时翻译…");
    }

    private async Task RunAsync()
    {
        try { await Task.WhenAll(CaptureLoopAsync(), RecognitionLoopAsync(), TranslationLoopAsync()).ConfigureAwait(false); }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        finally { _frames.Dispose(); _source.Dispose(); _cache.Clear(); _cacheOrder.Clear(); }
    }

    private async Task CaptureLoopAsync()
    {
        var token = _stop.Token;
        try
        {
            var suspended = false;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                bool paused;
                lock (_gate) paused = _paused;
                var available = !paused && _canCapture();
                if (!available)
                {
                    if (!suspended) { _source.Suspend(); suspended = true; }
                    lock (_gate)
                    {
                        if (_available)
                        {
                            _available = false; _version++; _pending = null; _current = null;
                            _anchors = null; _latestScene = null; _lastOutput = null; _translationCancel?.Cancel(); HideLocked();
                            _recognitionCancel?.Cancel();
                        }
                    }
                    if (!paused) StatusChanged?.Invoke("翻译选区暂不可见，已暂停采集");
                    await Task.Delay(400, token).ConfigureAwait(false);
                    continue;
                }
                lock (_gate) _available = true;
                suspended = false;
                var frame = await _source.CaptureAsync(token).ConfigureAwait(false);
                if (frame is not null)
                {
                    try
                    {
                        Anchors? anchors;
                        var scene = new Scene(frame.Bounds, RealtimeImageProbe.Read(frame.Image.Bitmap));
                        lock (_gate)
                        {
                            anchors = _anchors;
                            if (_latestScene is { } previous)
                            {
                                var moved = previous.Bounds != scene.Bounds || (anchors is { Areas.Length: > 0 }
                                    ? anchors.Areas.Any(area => !previous.Probe.Crop(area).MatchesGlyphs(scene.Probe.Crop(area)))
                                    : !previous.Probe.Matches(scene.Probe));
                                if (moved)
                                {
                                    _motionFrames = _motionFrames == 0 || _clock.GetElapsedTime(_lastMotion) > TimeSpan.FromMilliseconds(650)
                                        ? 1 : _motionFrames + 1;
                                    _lastMotion = _clock.GetTimestamp();
                                    _continuousMotion = _motionFrames >= 2;
                                }
                                else if (_motionFrames > 0 && _clock.GetElapsedTime(_lastMotion) >= SettleTime)
                                { _motionFrames = 0; _continuousMotion = false; }
                            }
                            _latestScene = scene;
                        }
                        if (anchors is not null)
                        {
                            var same = MatchesScene(anchors, scene);
                            lock (_gate)
                            {
                                if (ReferenceEquals(anchors, _anchors))
                                {
                                    _visualValid = same;
                                    if (_current is { } movingWork)
                                        foreach (var region in movingWork.Regions.Where(region => !IsBlockCurrent(region)))
                                            MarkMovingLocked(region);
                                    if (!same) MarkSceneChangedLocked();
                                    else if (_awaitingStableScene && _clock.GetElapsedTime(_lastSceneChange) >= SettleTime)
                                        _awaitingStableScene = false;
                                    if (_lastOutput is { } output && _current is { } current && !_paused && _available)
                                        PublishLocked(current, output.Blocks.Select(block => block.Translation).ToArray(), output.Status, output.HasFailures);
                                }
                            }
                        }
                        _frames.Publish(frame);
                    }
                    catch { frame.Dispose(); throw; }
                }
                else
                {
                    // WGC need not produce another frame once the source is
                    // static. Expire reading/motion holds on the active capture
                    // cadence as well, otherwise the final translation stalls.
                    lock (_gate)
                    {
                        if (!_paused && _available && _lastOutput is { } output && _current is { } current &&
                            _anchors is { } currentAnchors && _latestScene is { } latest)
                        {
                            _visualValid = MatchesScene(currentAnchors, latest);
                            if (_visualValid && _clock.GetElapsedTime(_lastSceneChange) >= SettleTime)
                                _awaitingStableScene = false;
                            if (_clock.GetElapsedTime(_lastMotion) >= SettleTime)
                            { _motionFrames = 0; _continuousMotion = false; }
                            PublishLocked(current, output.Blocks.Select(block => block.Translation).ToArray(), output.Status, output.HasFailures);
                        }
                    }
                }
                await Task.Delay(Volatile.Read(ref _sampleMilliseconds), token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception) { Fail("画面采集已停止：" + exception.Message); }
    }

    private async Task RecognitionLoopAsync()
    {
        var token = _stop.Token;
        RealtimeImageProbe? lastProbe = null;
        ScreenRegion lastBounds = default;
        long lastVersion = -1;
        try
        {
            while (true)
            {
                using var frame = await _frames.TakeAsync(token).ConfigureAwait(false);
                using var recognition = CancellationTokenSource.CreateLinkedTokenSource(token);
                long startingVersion;
                lock (_gate)
                {
                    if (_paused || !_available) continue;
                    startingVersion = _version;
                }
                var probe = RealtimeImageProbe.Read(frame.Image.Bitmap);
                if (lastVersion == startingVersion && frame.Bounds == lastBounds && lastProbe?.Matches(probe) == true) continue;
                var watch = Stopwatch.StartNew();
                StatusChanged?.Invoke("正在识别变化的文字…");
                OcrRecognitionResult result;
                lock (_gate)
                {
                    if (_paused || !_available || startingVersion != _version) continue;
                    _recognitionCancel = recognition;
                }
                try { result = await _recognize(frame.Image, recognition.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!token.IsCancellationRequested && recognition.IsCancellationRequested) { continue; }
                finally { lock (_gate) { if (ReferenceEquals(_recognitionCancel, recognition)) _recognitionCancel = null; } }
                token.ThrowIfCancellationRequested();
                // Actual throughput controls cadence; original OCR resolution/model stay unchanged.
                Volatile.Write(ref _sampleMilliseconds, Math.Clamp((int)watch.ElapsedMilliseconds / 3, 120, 400));
                if (!result.IsSuccess) { Fail(result.ErrorMessage ?? "识别失败，请检查内容识别设置。"); return; }
                var regions = GroupLines(TranslationInkBounds.Refine(frame.Image.Bitmap,
                    TranslationPresentationLayout.TightenToWordBounds(result.Regions, result.Words)));
                var appearance = regions.Select(region => RealtimeTextAppearance.Read(frame.Image.Bitmap, region)).ToArray();
                var areas = result.Regions.Select(r => Rectangle.FromLTRB((int)Math.Floor(r.X), (int)Math.Floor(r.Y),
                    (int)Math.Ceiling(r.X + r.Width), (int)Math.Ceiling(r.Y + r.Height))).ToArray();
                var anchors = new Anchors(frame.Bounds, areas, areas.Select(probe.Crop).ToArray());
                lock (_gate)
                {
                    if (_paused || !_available || startingVersion != _version) continue;
                    var same = _current is { } current && current.Regions.Select(x => x.Text).SequenceEqual(regions.Select(x => x.Text));
                    _anchors = anchors;
                    // OCR can finish after another frame has arrived. Validate against that
                    // newest scene before showing anything from the older recognized frame.
                    _visualValid = _latestScene is { } latest && MatchesScene(anchors, latest);
                    if (!same)
                    {
                        if (_current is not null) MarkSceneChangedLocked();
                        _version++;
                        _lastOutput = null;
                        // Keep useful in-flight paragraphs when a clock, counter or
                        // animated label changes elsewhere. Only the newest placement
                        // may be published; completed text is reused by the next work.
                        if (!_continuousMotion && (_current is null || !regions.Any(region => _current.Regions.Any(old => old.Text == region.Text))))
                            _translationCancel?.Cancel();
                    }
                    _current = new(_version, frame.Bounds, frame.Image.PixelWidth, frame.Image.PixelHeight, regions, appearance);
                    if (!same)
                    {
                        _pending = _current;
                        // A changing clock/carousel must not blank other text.
                        // Reuse only context-matching cached paragraphs and
                        // validate their current glyphs before presenting them.
                        var cached = regions.Select((_, index) =>
                            _cache.TryGetValue(CacheKey(_current, index), out var text) ? text : string.Empty).ToArray();
                        if (cached.Any(text => text.Length > 0))
                            PublishLocked(_current, cached, "已显示缓存译文，正在翻译新文字…");
                        else HoldReadingLocked();
                        if (_textReady.CurrentCount == 0) _textReady.Release();
                    }
                    else if (_lastOutput is { } output)
                    {
                        // Same text in a moved window/scroll position needs placement only.
                        PublishLocked(_current, output.Blocks.Select(b => b.Translation).ToArray(), output.Status, output.HasFailures);
                    }
                    lastVersion = _version;
                }
                lastProbe = probe; lastBounds = frame.Bounds;
                // Merge a short burst of typewriter/scroll updates without starving new text.
                await Task.Delay(Math.Clamp(300 - (int)watch.ElapsedMilliseconds, 50, 220), token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception) { Fail("实时识别已停止：" + exception.Message); }
    }

    private async Task TranslationLoopAsync()
    {
        var token = _stop.Token;
        try
        {
            while (true)
            {
                await _textReady.WaitAsync(token).ConfigureAwait(false);
                Work work;
                using var request = CancellationTokenSource.CreateLinkedTokenSource(token);
                lock (_gate)
                {
                    if (_pending is not { } pending || _paused || !_available) continue;
                    work = pending; _pending = null; _translationCancel = request;
                }
                try { await TranslateAsync(work, request.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (request.IsCancellationRequested) { }
                catch (Exception exception) { StatusChanged?.Invoke("翻译失败，可点重试：" + exception.Message); }
                finally { lock (_gate) { if (ReferenceEquals(_translationCancel, request)) _translationCancel = null; } }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception) { Fail("实时翻译已停止：" + exception.Message); }
    }

    private async Task TranslateAsync(Work work, CancellationToken token)
    {
        var translated = new string[work.Regions.Count];
        var originals = work.Regions.Select(r => r.Text).ToArray();
        var protection = _glossary.Protect(originals);
        var prepared = protection.Segments;
        var pending = TranslationTargetLanguageMatcher.GetPendingSegmentIndices(originals, "auto", _target).ToHashSet();
        var missing = new List<int>();
        for (var i = 0; i < prepared.Length; i++)
        {
            if (_glossary.TryExact(work.Regions[i].Text, out var exact)) translated[i] = exact;
            else if (protection.TryResolveOnlyTerms(i, out var termsOnly)) translated[i] = termsOnly;
            else if (!pending.Contains(i)) translated[i] = originals[i];
            else if (TryGetCached(CacheKey(work, i), out var cached)) translated[i] = cached;
            else missing.Add(i);
        }
        var notice = originals.Length == 0 ? "选区中未识别到文字，正在等待画面变化…"
            : pending.Count == 0 && translated.SequenceEqual(originals) ? "选区文字已是目标语言，正在等待新内容…"
            : "译文已更新";
        if (missing.Count > 0)
        {
            // Stable labels and exact glossary matches do not wait for new prose.
            lock (_gate)
                if (_current?.Version == work.Version && !_paused && _available && !token.IsCancellationRequested &&
                    translated.Where((text, index) => !string.IsNullOrEmpty(text) && text != originals[index]).Any())
                    PublishLocked(_current, translated, "已显示缓存译文，正在翻译新文字…");
            using var context = new RealtimeTranslationContext(string.Join('\n', originals), protection);
            using var partial = TranslationSegmentProgress.Begin((source, text) =>
            {
                lock (_gate)
                {
                    if (token.IsCancellationRequested || _disposed) return;
                    foreach (var i in missing.Where(i => prepared[i] == source))
                    {
                        if (!protection.TryRestore(i, text, out var restored)) continue;
                        translated[i] = restored;
                        Remember(CacheKey(work, i), restored);
                    }
                    if (!_paused && _available)
                    {
                        if (_current?.Version == work.Version) PublishLocked(_current, translated, "正在翻译剩余文字…");
                        else if (_continuousMotion) PublishReadingWorkLocked(work, translated, "正在翻译剩余文字…");
                    }
                }
            });
            using var progress = TranslationProgress.Begin(message => { if (!token.IsCancellationRequested) StatusChanged?.Invoke(message); });
            StatusChanged?.Invoke($"正在翻译 {missing.Count} 段新文字…");
            TranslationSegmentsResult result;
            try
            {
                result = await _provider.TranslateSegmentsAsync(missing.Select(i => prepared[i]).ToArray(), "auto", _target, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                // Completed, validated paragraphs have already been delivered.
                // Finish this update with an explicit failure instead of leaving
                // the overlay permanently saying that translation is running.
                result = TranslationSegmentsResult.Failure(exception.Message);
            }
            token.ThrowIfCancellationRequested();
            var failed = result.FailedSegmentIndices?.ToHashSet() ?? [];
            for (var j = 0; j < missing.Count; j++)
            {
                var i = missing[j];
                if (j >= result.Segments.Count || failed.Contains(j) ||
                    (!result.IsSuccess && result.FailedSegmentIndices is null) || string.IsNullOrWhiteSpace(result.Segments[j])) continue;
                if (!protection.TryRestore(i, result.Segments[j], out var text)) { notice = "部分译文未保留完整术语，可重试或更换翻译方式"; continue; }
                translated[i] = text;
                var key = CacheKey(work, i);
                Remember(key, text);
            }
            var incomplete = missing.Count(index => string.IsNullOrWhiteSpace(translated[index]));
            var messages = new List<string>();
            if (incomplete > 0) messages.Add(notice == "译文已更新" ? $"{incomplete} 段尚未完成，可重试" : notice);
            if (!string.IsNullOrWhiteSpace(result.ErrorMessage)) messages.Add(result.ErrorMessage);
            if (!string.IsNullOrWhiteSpace(result.Notice)) messages.Add(result.Notice);
            if (messages.Count > 0) notice = string.Join('\n', messages.Distinct());
        }
        lock (_gate)
        {
            if (_paused || !_available || token.IsCancellationRequested) return;
            if (_current?.Version != work.Version)
            {
                if (_continuousMotion) PublishReadingWorkLocked(work, translated, notice, missing.Any(index => string.IsNullOrWhiteSpace(translated[index])));
                return;
            }
            PublishLocked(_current, translated, notice, missing.Any(index => string.IsNullOrWhiteSpace(translated[index])));
        }
    }

    // Include immediate context to disambiguate short labels, but keep long stable paragraphs reusable.
    private void Remember(string key, string text)
    {
        lock (_gate)
        {
            if (_cache.TryAdd(key, text)) _cacheOrder.Enqueue(key);
            while (_cacheOrder.Count > 512) _cache.Remove(_cacheOrder.Dequeue());
        }
    }

    private bool TryGetCached(string key, out string text)
    { lock (_gate) return _cache.TryGetValue(key, out text!); }

    private static string CacheKey(Work work, int index)
    {
        var text = work.Regions[index].Text;
        return text.Length >= 80 ? text : text + "\u001e" + string.Join('\u001f',
            work.Regions.Skip(Math.Max(0, index - 1)).Take(index == 0 ? 2 : 3).Select(r => r.Text));
    }

    private void PublishLocked(Work work, IReadOnlyList<string> translated, string status, bool hasFailures = false)
    {
        var blocks = work.Regions.Select((region, index) => new RealtimeTranslationBlock(region,
            index < translated.Count ? translated[index] ?? string.Empty : string.Empty) { Appearance = work.Appearance[index] }).ToArray();
        _lastOutput = new(work.Bounds, work.Width, work.Height, blocks, status) { HasFailures = hasFailures };
        _movingRegions.RemoveAll(entry => _clock.GetElapsedTime(entry.Changed) >= SettleTime);
        var moving = blocks.Select(block => !IsBlockCurrent(block.Region) || (_presentedOutput is not null || _continuousMotion) &&
            _movingRegions.Any(entry => entry.Region.Text == block.Region.Text || Overlaps(entry.Region, block.Region))).ToArray();
        var fixedBlocks = blocks.Select((block, index) => moving[index] ? block with { Translation = string.Empty } : block).ToArray();
        var movingBlocks = blocks.Where((_, index) => moving[index]).ToArray();
        if (moving.Any(value => value) && HasTranslation(_lastOutput with { Blocks = fixedBlocks }))
        {
            // Static paragraphs stay in place even when another column keeps
            // moving. Only moving text goes to the stable reading area.
            var candidate = _lastOutput with { Blocks = movingBlocks };
            if (HasTranslation(candidate) && (_readingOutput is null ||
                _clock.GetElapsedTime(_readingSince) >= MinimumReadingTime && !SameTranslatedText(_readingOutput, candidate)))
            { _readingOutput = candidate; _readingSince = _clock.GetTimestamp(); }
            var reading = _readingOutput?.Blocks.Where(block => !fixedBlocks.Any(current =>
                !string.IsNullOrWhiteSpace(current.Translation) && current.Region.Text == block.Region.Text)).ToArray() ?? [];
            PublishVisibleLocked(_lastOutput with { Blocks = fixedBlocks, ReadingBlocks = reading,
                Status = reading.Length > 0 ? ReadingStatus + "\n" + status : status });
            return;
        }
        if (moving.Any(value => value)) _visualValid = false;
        if (!_visualValid)
        {
            blocks = fixedBlocks;
            if (blocks.All(block => string.IsNullOrWhiteSpace(block.Translation)))
            {
                if (_continuousMotion) PublishReadingLocked(_lastOutput);
                else HoldReadingLocked();
                return;
            }
        }
        var visible = _lastOutput with { Blocks = blocks };
        if (_readingOutput is not null)
        {
            // Keep a readable, explicitly historical subtitle during motion.
            // Even a fast cached OCR result must not flash back at a moving
            // coordinate for just one frame. No extra frame buffer is retained.
            if (_awaitingStableScene || _clock.GetElapsedTime(_readingSince) < MinimumReadingTime)
            {
                if (HasTranslation(visible) && _clock.GetElapsedTime(_readingSince) >= MinimumReadingTime &&
                    !SameTranslatedText(_readingOutput, visible))
                {
                    _readingOutput = visible with { IsReadingSnapshot = true, Status = ReadingStatus + "\n" + visible.Status };
                    _readingSince = _clock.GetTimestamp();
                    PublishVisibleLocked(_readingOutput);
                }
                return;
            }
            _readingOutput = null;
        }
        PublishVisibleLocked(visible);
    }

    private static bool Overlaps(OcrTextRegion first, OcrTextRegion second)
    {
        var width = Math.Max(0, Math.Min(first.X + first.Width, second.X + second.Width) - Math.Max(first.X, second.X));
        var height = Math.Max(0, Math.Min(first.Y + first.Height, second.Y + second.Height) - Math.Max(first.Y, second.Y));
        return width * height >= Math.Min(first.Width * first.Height, second.Width * second.Height) * .5;
    }

    private void MarkMovingLocked(OcrTextRegion region)
    {
        _movingRegions.RemoveAll(entry => entry.Region.Text == region.Text || Overlaps(entry.Region, region) ||
            _clock.GetElapsedTime(entry.Changed) >= SettleTime);
        if (_movingRegions.Count >= 256) _movingRegions.RemoveAt(0);
        _movingRegions.Add((region, _clock.GetTimestamp()));
    }

    private static bool HasTranslation(RealtimeTranslationUpdate output) => output.Blocks.Any(block =>
        !string.IsNullOrWhiteSpace(block.Translation) && block.Translation.Trim() != block.Region.Text.Trim());

    private static bool SameTranslatedText(RealtimeTranslationUpdate first, RealtimeTranslationUpdate second) =>
        first.Blocks.Select(block => block.Translation).SequenceEqual(second.Blocks.Select(block => block.Translation));

    private void MarkSceneChangedLocked()
    {
        _awaitingStableScene = true;
        _lastSceneChange = _clock.GetTimestamp();
    }

    private void HoldReadingLocked()
    {
        if (_readingOutput is not null) return;
        if (_presentedOutput is { } previous && HasTranslation(previous))
        {
            _readingOutput = previous with { IsReadingSnapshot = true, Status = ReadingStatus + "\n" + previous.Status };
            _readingSince = _clock.GetTimestamp();
            PublishVisibleLocked(_readingOutput);
        }
    }

    private void PublishReadingWorkLocked(Work work, IReadOnlyList<string> translated, string status, bool hasFailures = false)
    {
        var completed = new RealtimeTranslationUpdate(work.Bounds, work.Width, work.Height, work.Regions.Select((region, index) =>
            new RealtimeTranslationBlock(region, index < translated.Count ? translated[index] ?? string.Empty : string.Empty)).ToArray(), status)
            { HasFailures = hasFailures };
        if (_current is { } current)
        {
            var cached = current.Regions.Select((_, index) => _cache.TryGetValue(CacheKey(current, index), out var text) ? text : string.Empty).ToArray();
            if (current.Regions.Where((region, index) => cached[index].Length > 0 && cached[index] != region.Text && IsBlockCurrent(region)).Any())
            {
                // A slow old request can still supply useful static paragraphs.
                // Do not replace them all with a historical subtitle merely
                // because a neighboring animated label changed its OCR text.
                if (HasTranslation(completed) && (_readingOutput is null || _clock.GetElapsedTime(_readingSince) >= MinimumReadingTime))
                { _readingOutput = completed; _readingSince = _clock.GetTimestamp(); }
                PublishLocked(current, cached, status, hasFailures);
                return;
            }
        }
        PublishReadingLocked(completed);
    }

    private void PublishReadingLocked(RealtimeTranslationUpdate output)
    {
        if (!HasTranslation(output)) { HoldReadingLocked(); return; }
        if (_readingOutput is not null && (_clock.GetElapsedTime(_readingSince) < MinimumReadingTime || SameTranslatedText(_readingOutput, output))) return;
        _readingOutput = output with { IsReadingSnapshot = true, Status = ReadingStatus + "\n" + output.Status };
        _readingSince = _clock.GetTimestamp();
        PublishVisibleLocked(_readingOutput);
    }

    private void PublishVisibleLocked(RealtimeTranslationUpdate visible)
    {
        if (!_hidden && _presentedOutput is { } previous && previous.Bounds == visible.Bounds &&
            previous.Status == visible.Status && previous.IsReadingSnapshot == visible.IsReadingSnapshot &&
            previous.Blocks.SequenceEqual(visible.Blocks) && previous.ReadingBlocks.SequenceEqual(visible.ReadingBlocks)) return;
        _hidden = false;
        _presentedOutput = visible;
        Updated?.Invoke(visible);
    }
    private bool IsBlockCurrent(OcrTextRegion region)
    {
        if (_anchors is not { } anchors || _latestScene is not { } scene || anchors.Bounds != scene.Bounds) return false;
        var area = new RectangleF((float)region.X, (float)region.Y, (float)region.Width, (float)region.Height);
        var indices = Enumerable.Range(0, anchors.Areas.Length).Where(i => area.IntersectsWith(anchors.Areas[i])).ToArray();
        return indices.Length > 0 && indices.All(i => anchors.Probes[i].MatchesGlyphs(scene.Probe.Crop(anchors.Areas[i])));
    }
    private void HideLocked()
    {
        _readingOutput = null; _awaitingStableScene = false;
        _continuousMotion = false; _motionFrames = 0;
        _movingRegions.Clear();
        _presentedOutput = null;
        if (_hidden) return;
        _hidden = true; Invalidated?.Invoke();
    }
    private void Fail(string message) { StatusChanged?.Invoke(message); _stop.Cancel(); lock (_gate) HideLocked(); }

    private static bool MatchesScene(Anchors anchors, Scene scene) => scene.Bounds == anchors.Bounds &&
        anchors.Areas.Select((area, i) => anchors.Probes[i].MatchesGlyphs(scene.Probe.Crop(area))).All(value => value);

    internal static IReadOnlyList<OcrTextRegion> GroupLines(IReadOnlyList<OcrTextRegion> regions) =>
        RealtimeParagraphLayout.Group(regions);

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true; _stop.Cancel();
        await Completion.ConfigureAwait(false);
        if (_completion is null) { _frames.Dispose(); _source.Dispose(); }
        _stop.Dispose(); _textReady.Dispose();
        lock (_gate) { _pending = null; _current = null; _anchors = null; _latestScene = null; _lastOutput = null; _presentedOutput = null; _readingOutput = null; }
        _movingRegions.Clear();
        Updated = null; Invalidated = null; StatusChanged = null;
    }
}
