using System.Collections.Concurrent;
using System.Drawing;
using System.Threading.Channels;
using Screenshot.App.Capture;
using Screenshot.App.Core;
using Screenshot.App.Presentation;
using Screenshot.App.Text;
using Screenshot.App.Text.Realtime;

namespace Screenshot.App.Tests;

public sealed class RealtimeTranslationTests
{
    [Fact]
    public void SettingsAndNumericHotkeyRoundTrip()
    {
        var settings = AppSettings.CreateDefault() with { RealtimeTranslationHotKey = "Ctrl+1",
            RealtimeTranslation = new() { Scope = RealtimeTranslationScope.Region, Display = RealtimeTranslationDisplay.Subtitle,
                FontSize = 26, Glossary = "Mana=法力值" } };
        var saved = new SettingsViewModel(settings).CreateSettings().Normalize();
        Assert.Equal(settings.RealtimeTranslation, saved.RealtimeTranslation);
        Assert.Contains(HotKeyConfiguration.CreateBindings(saved), b => b.Action == HotKeyAction.RealtimeTranslation && b.Gesture.ToString() == "Ctrl+1");
        Assert.True(HotKeyConfiguration.Validate(HotKeyConfiguration.CreateBindings(saved)).IsValid);
        Assert.False(HotKeyConfiguration.Validate(HotKeyConfiguration.CreateBindings(saved with { PinHotKey = "Ctrl+1" })).IsValid);
        var json = System.Text.Json.JsonSerializer.Serialize(saved);
        Assert.Equal(saved.RealtimeTranslation, System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json)!.RealtimeTranslation);
    }

    [Fact]
    public void InvalidPresentationSettingsAreNormalized()
    {
        var settings = new RealtimeTranslationSettings { FontSize = double.NaN, BackgroundOpacity = 5,
            SubtitleX = -1, SubtitleY = double.PositiveInfinity, Display = (RealtimeTranslationDisplay)999 }.Normalize();
        Assert.Equal(18, settings.FontSize); Assert.Equal(1, settings.BackgroundOpacity);
        Assert.Equal(0, settings.SubtitleX); Assert.Equal(.85, settings.SubtitleY);
        Assert.Equal(RealtimeTranslationDisplay.Overlay, settings.Display);
    }

    [Fact]
    public void GlossaryUsesLongestWholeWordAndVerifiesTerminology()
    {
        var glossary = new RealtimeGlossary("Mana=法力值\nMana Shield=法力护盾\nC++=C++\n生命=生命值");
        var protectedTerms = glossary.Protect(["Mana Shield costs Mana, not Manager. C++ 生命"]);
        Assert.True(protectedTerms.TryRestore(0, protectedTerms.Segments[0], out var restored));
        Assert.Equal("法力护盾 costs 法力值, not Manager. C++ 生命值", restored);
        Assert.True(glossary.TryExact("Mana", out var result)); Assert.Equal("法力值", result);
        Assert.False(protectedTerms.TryRestore(0, "获得魔法盾", out _));
    }

    [Fact]
    public void ProtectedTermsKeepReorderedRepeatedWordsAndRejectMissingTokens()
    {
        var protection = new RealtimeGlossary("player=玩家\nshield=护盾").Protect(["The player needs a shield for the player."]);
        Assert.True(protection.TryRestore(0, "ZXQTERMAQXZ 需要 ZXQTERMBQXZ 来保护 ZXQTERMAQXZ。", out var result));
        Assert.Equal("玩家 需要 护盾 来保护 玩家。", result);
        Assert.False(protection.TryRestore(0, "ZXQTERMAQXZ 需要 ZXQTERMBQXZ。", out _));
        using (new RealtimeTranslationContext("original context", protection))
            Assert.Equal("The player needs a shield for the player.", RealtimeTranslationContext.RestoreSourceText(protection.Segments[0]));
        Assert.Equal(protection.Segments[0], RealtimeTranslationContext.RestoreSourceText(protection.Segments[0]));
        var collision = new RealtimeGlossary("player=玩家").Protect(["player ZXQTERMAQXZ"]);
        Assert.True(collision.TryRestore(0, collision.Segments[0], out var collisionResult));
        Assert.Equal("玩家 ZXQTERMAQXZ", collisionResult);
    }

    [Fact]
    public void GlossaryValidationChecksRestoredLanguageAndNumbersWithoutRewritingOutputLanguageDetection()
    {
        var protection = new RealtimeGlossary("Health=生命值\nMana=法力值").Protect(["Health / Mana"]);
        using (new RealtimeTranslationContext("Health / Mana", protection))
        {
            Assert.Null(TranslationCompletenessValidator.Validate(protection.Segments[0], protection.Segments[0], "zh-Hans"));
            Assert.NotNull(TranslationCompletenessValidator.Validate(protection.Segments[0], "ZXQTERMAQXZ", "zh-Hans"));
            Assert.Equal("已经恢复 ZXQTERMAQXZ", RealtimeTranslationContext.RestoreSourceText("已经恢复 ZXQTERMAQXZ"));
            Assert.Contains("ZXQTERM", LocalLargeModelTranslationProvider.CreatePrompt(protection.Segments[0], "en", "zh"));
        }
        Assert.DoesNotContain("Placeholder meanings", LocalLargeModelTranslationProvider.CreatePrompt("Hello world", "en", "zh"));
    }

    [Fact]
    public void RequestContextDoesNotLeakAfterDisposal()
    {
        Assert.Empty(RealtimeTranslationContext.Instructions);
        using (new RealtimeTranslationContext("Mana Shield"))
            Assert.Contains("Mana Shield", RealtimeTranslationContext.Instructions);
        Assert.Empty(RealtimeTranslationContext.Instructions);
    }

    [Fact]
    public async Task MailboxReleasesSupersededFramesAndKeepsOnlyLatest()
    {
        using var mailbox = new LatestFrameMailbox();
        var first = Frame(Color.Black); var second = Frame(Color.White);
        mailbox.Publish(first); mailbox.Publish(second);
        Assert.ThrowsAny<Exception>(() => first.Image.Bitmap.GetPixel(0, 0));
        using var token = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        using var taken = await mailbox.TakeAsync(token.Token);
        Assert.Same(second, taken);
    }

    [Fact]
    public async Task UnchangedPixelsDoNotRepeatOcrAndStopDisposesSource()
    {
        var source = new Source(); var ocrCount = 0; var provider = new Provider();
        await using (var session = Session(source, provider, (_, _) => { Interlocked.Increment(ref ocrCount); return Task.FromResult(Ocr("Open inventory")); }))
        {
            var output = new TaskCompletionSource<RealtimeTranslationUpdate>(TaskCreationOptions.RunContinuationsAsynchronously);
            session.Updated += value => output.TrySetResult(value); session.Start(); source.Send(Frame(Color.White));
            await output.Task.WaitAsync(TimeSpan.FromSeconds(4));
            source.Send(Frame(Color.White)); source.Send(Frame(Color.White));
            await Task.Delay(550);
            Assert.Equal(1, Volatile.Read(ref ocrCount)); Assert.Equal(1, provider.Calls);
        }
        Assert.True(source.Disposed);
    }

    [Fact]
    public async Task LateTranslationCannotReplaceNewTextEvenIfProviderIgnoresCancellation()
    {
        var source = new Source(); var releaseOld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var newRecognized = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new Provider(async (segments, _) =>
        {
            if (segments[0] == "Old quest") { entered.TrySetResult(); await releaseOld.Task; return new(true, ["旧任务"], null); }
            return new(true, ["新任务"], null);
        });
        await using var session = Session(source, provider, (image, _) =>
        {
            var old = image.Bitmap.GetPixel(0, 0).R > 100;
            if (!old) newRecognized.TrySetResult();
            return Task.FromResult(Ocr(old ? "Old quest" : "New quest"));
        });
        var values = new ConcurrentQueue<string>();
        var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Updated += value => { foreach (var block in value.Blocks) values.Enqueue(block.Translation); if (value.Blocks.Any(x => x.Translation == "新任务")) complete.TrySetResult(); };
        session.Start(); source.Send(Frame(Color.White));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        source.Send(Frame(Color.Black)); await newRecognized.Task.WaitAsync(TimeSpan.FromSeconds(3));
        releaseOld.TrySetResult(); await complete.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.DoesNotContain("旧任务", values); Assert.Contains("新任务", values);
    }

    [Fact]
    public async Task OcrFinishingAfterSceneChangedCannotFlashItsOldTranslation()
    {
        var source = new Source(); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        await using var session = Session(source, new Provider((segments, _) => Task.FromResult(new TranslationSegmentsResult(true,
            [segments[0] == "Old quest" ? "旧任务" : "新任务"], null))), async (_, token) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            { entered.TrySetResult(); await release.Task.WaitAsync(token); return Ocr("Old quest"); }
            return Ocr("New quest");
        });
        var texts = new ConcurrentQueue<string>();
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Updated += result =>
        {
            foreach (var block in result.Blocks) texts.Enqueue(block.Translation);
            if (result.Blocks.Any(block => block.Translation == "新任务")) completed.TrySetResult();
        };
        session.Start(); source.Send(Frame(Color.White)); await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        source.Send(Frame(Color.Black)); await Until(() => source.Captures >= 2); await Task.Delay(40);
        release.TrySetResult(); await completed.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.DoesNotContain("旧任务", texts);
    }

    [Fact]
    public async Task MixedLanguageAndExactGlossarySkipUnneededRequests()
    {
        var source = new Source(); var provider = new Provider();
        await using var session = new RealtimeTranslationSession(source, (_, _) => Task.FromResult(new OcrRecognitionResult(true, "", null)
        { Regions = [new("中文正文", 0, 0, 100, 12), new("Mana", 0, 30, 100, 12), new("Open inventory", 0, 60, 100, 12)] }),
            provider, "zh-Hans", new("Mana=法力值"));
        var completion = new TaskCompletionSource<RealtimeTranslationUpdate>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Updated += result => { if (result.Blocks.All(block => !string.IsNullOrEmpty(block.Translation))) completion.TrySetResult(result); };
        session.Start(); source.Send(Frame(Color.White));
        var output = await completion.Task.WaitAsync(TimeSpan.FromSeconds(4));
        Assert.Equal(new[] { "Open inventory" }, provider.LastInput);
        Assert.Equal("法力值", output.Blocks[1].Translation);
        Assert.Equal("中文正文", output.Blocks[0].Translation);
    }

    [Fact]
    public async Task StableTextAtNewPositionReusesTranslation()
    {
        var source = new Source(); var provider = new Provider(); var calls = 0; var clock = new ManualClock();
        await using var session = new RealtimeTranslationSession(source, (_, _) => Task.FromResult(Ocr("Open inventory") with
        { Regions = [new("Open inventory", 0, Interlocked.Increment(ref calls) * 10, 100, 16)] }), provider, "zh-Hans", new(""), clock: clock);
        var output = new ConcurrentQueue<RealtimeTranslationUpdate>();
        var invalidated = 0;
        session.Invalidated += () => Interlocked.Increment(ref invalidated);
        session.Updated += output.Enqueue; session.Start(); source.Send(Frame(Color.White));
        await Until(() => output.Count > 0);
        source.Send(Frame(Color.Black)); await Until(() => calls >= 2 && output.Any(value => value.IsReadingSnapshot));
        Assert.Equal(10, output.Last().Blocks[0].Region.Y);
        clock.Advance(1600); source.Send(null); // WGC emits no extra frame after the screen becomes static
        await Until(() => !output.Last().IsReadingSnapshot && output.Last().Blocks[0].Region.Y == 20);
        Assert.Equal(1, provider.Calls); Assert.Equal(20, output.Last().Blocks[0].Region.Y);
        Assert.Equal(0, invalidated);
    }

    [Fact]
    public async Task NeverSettlingCarouselStillPublishesReadableTranslationsAndReleasesOnPause()
    {
        var source = new Source(); var clock = new ManualClock(); var calls = 0;
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new Provider(async (segments, token) =>
        { await finish.Task.WaitAsync(token); return new(true, segments.Select(text => "译文 " + text).ToArray(), null); });
        await using var session = new RealtimeTranslationSession(source, (_, _) => Task.FromResult(Ocr("Read this changing document paragraph " + Interlocked.Increment(ref calls))),
            provider, "zh-Hans", new(""), clock: clock);
        var outputs = new ConcurrentQueue<RealtimeTranslationUpdate>(); var invalidated = 0;
        session.Updated += outputs.Enqueue; session.Invalidated += () => Interlocked.Increment(ref invalidated);
        session.Start(); source.Send(Frame(Color.White)); await Until(() => provider.Calls == 1);
        for (var i = 0; i < 4; i++)
        {
            clock.Advance(200); source.Send(Frame(i % 2 == 0 ? Color.Black : Color.White));
            var expected = i + 2; await Until(() => calls >= expected);
        }
        finish.TrySetResult(); await Until(() => outputs.Any(value => value.IsReadingSnapshot));
        Assert.Contains(outputs.Last().Blocks, block => block.Translation.StartsWith("译文", StringComparison.Ordinal));
        var first = outputs.Last();
        for (var i = 0; i < 3; i++)
        {
            clock.Advance(200); source.Send(Frame(i % 2 == 0 ? Color.Black : Color.White));
            var expected = i + 6; await Until(() => calls >= expected);
        }
        Assert.Same(first, outputs.Last()); // enough time to read, despite newer OCR/model results
        Assert.Equal(0, invalidated);
        session.SetPaused(true);
        Assert.Equal(1, invalidated);
    }

    [Fact]
    public async Task ChangingAnotherLabelDoesNotClearStableParagraph()
    {
        var stable = "This paragraph contains enough unchanged words to remain independently reusable while the adjacent clock keeps changing.";
        var source = new Source(); var count = 0; var outputs = new ConcurrentQueue<RealtimeTranslationUpdate>(); var invalidated = 0;
        var provider = new Provider((segments, _) => Task.FromResult(new TranslationSegmentsResult(true,
            segments.Select(text => text == stable ? "这段正文保持显示。" : "时钟").ToArray(), null)));
        await using var session = new RealtimeTranslationSession(source, (_, _) => Task.FromResult(new OcrRecognitionResult(true, "", null)
        { Regions = [new(stable, 0, 0, 150, 15), new("Clock " + Interlocked.Increment(ref count), 0, 70, 80, 15)] }), provider, "zh-Hans", new(""));
        session.Updated += outputs.Enqueue; session.Invalidated += () => Interlocked.Increment(ref invalidated);
        session.Start(); source.Send(Frame(Color.White)); await Until(() => outputs.Count > 0);
        using var frame = Frame(Color.White);
        using (var graphics = Graphics.FromImage(frame.Image.Bitmap)) graphics.FillRectangle(Brushes.Black, 0, 65, 160, 35);
        source.Send(new(new CapturedImage((Bitmap)frame.Image.Bitmap.Clone()), frame.Bounds));
        await Until(() => count >= 2);
        Assert.All(outputs, update => Assert.Equal("这段正文保持显示。", update.Blocks[0].Translation));
        Assert.Equal(0, invalidated);
    }

    private sealed class ManualClock : TimeProvider
    {
        private long _milliseconds;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Interlocked.Read(ref _milliseconds);
        internal void Advance(long milliseconds) => Interlocked.Add(ref _milliseconds, milliseconds);
    }

    [Fact]
    public async Task SlowResultDuringContinuousAnimationKeepsStaticParagraphInPlace()
    {
        var stable = "This unchanged paragraph has enough surrounding context to be translated independently while another label moves on screen.";
        var source = new Source(); var clock = new ManualClock(); var calls = 0;
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new Provider(async (segments, token) =>
        { await finish.Task.WaitAsync(token); return new(true, segments.Select(text => text == stable ? "正文保持原位。" : "移动文字。").ToArray(), null); });
        await using var session = new RealtimeTranslationSession(source, (_, _) => Task.FromResult(new OcrRecognitionResult(true, "", null)
        { Regions = [new(stable, 0, 0, 150, 15), new("Please read this moving label " + Interlocked.Increment(ref calls), 0, 70, 140, 15)] }),
            provider, "zh-Hans", new(""), clock: clock);
        var updates = new ConcurrentQueue<RealtimeTranslationUpdate>(); session.Updated += updates.Enqueue;
        session.Start(); source.Send(Frame(Color.White)); await Until(() => provider.Calls == 1);
        for (var i = 0; i < 4; i++)
        {
            var frame = Frame(Color.White);
            using (var graphics = Graphics.FromImage(frame.Image.Bitmap))
                graphics.FillRectangle(i % 2 == 0 ? Brushes.Black : Brushes.White, 0, 65, 160, 35);
            clock.Advance(200); source.Send(frame);
            var expected = i + 2; await Until(() => calls >= expected);
        }
        finish.TrySetResult();
        await Until(() => updates.Any(update => update.Blocks[0].Translation == "正文保持原位。"));
        Assert.All(updates.Where(update => update.Blocks[0].Translation == "正文保持原位。"), update => Assert.False(update.IsReadingSnapshot));
    }

    [Fact]
    public async Task PauseCancelsTranslationAndResumeReadsNewFrame()
    {
        var source = new Source(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new Provider(async (_, token) =>
        {
            entered.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException) { cancelled.TrySetResult(); throw; }
            return new(true, [], null);
        });
        await using var session = Session(source, provider, (_, _) => Task.FromResult(Ocr("Open inventory")));
        session.Start(); source.Send(Frame(Color.White)); await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        session.SetPaused(true); await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task PauseCancelsRecognitionAndResumeStillProducesTranslation()
    {
        var source = new Source(); var count = 0;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var session = Session(source, new Provider(), async (_, token) =>
        {
            if (Interlocked.Increment(ref count) == 1)
            {
                entered.TrySetResult();
                try { await Task.Delay(Timeout.Infinite, token); }
                catch (OperationCanceledException) { cancelled.TrySetResult(); throw; }
            }
            return Ocr("Open inventory");
        });
        var result = new TaskCompletionSource<RealtimeTranslationUpdate>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Updated += value => result.TrySetResult(value);
        session.Start(); source.Send(Frame(Color.White)); await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        session.SetPaused(true); await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(3));
        session.SetPaused(false); source.Send(Frame(Color.Black));
        Assert.Equal("打开背包", (await result.Task.WaitAsync(TimeSpan.FromSeconds(3))).Blocks[0].Translation);
    }

    [Fact]
    public async Task GlossaryLabelsAppearBeforeSlowNewParagraphFinishes()
    {
        var source = new Source(); var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new Provider(async (_, token) => { await finish.Task.WaitAsync(token); return new(true, ["打开背包"], null); });
        await using var session = new RealtimeTranslationSession(source, (_, _) => Task.FromResult(new OcrRecognitionResult(true, "", null)
        { Regions = [new("Mana", 0, 0, 100, 12), new("Open inventory", 0, 60, 100, 12)] }), provider, "zh-Hans", new("Mana=法力值"));
        var first = new TaskCompletionSource<RealtimeTranslationUpdate>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Updated += value => first.TrySetResult(value); session.Start(); source.Send(Frame(Color.White));
        var result = await first.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal("法力值", result.Blocks[0].Translation); Assert.Empty(result.Blocks[1].Translation);
        finish.TrySetResult();
    }

    [Fact]
    public async Task RetryKeepsSuccessfulParagraphsAndRequestsOnlyTheFailedText()
    {
        var source = new Source(); var attempt = 0;
        var provider = new Provider((_, _) => Task.FromResult(Interlocked.Increment(ref attempt) == 1
            ? new TranslationSegmentsResult(true, ["打开背包", ""], "第二段失败") { FailedSegmentIndices = [1] }
            : new TranslationSegmentsResult(true, ["关闭背包"], null)));
        await using var session = new RealtimeTranslationSession(source, (_, _) => Task.FromResult(new OcrRecognitionResult(true, "", null)
        { Regions = [new("Open inventory", 0, 0, 100, 12), new("Close inventory", 0, 60, 100, 12)] }), provider, "zh-Hans", new(""));
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource<RealtimeTranslationUpdate>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Updated += result =>
        {
            if (result.Status.Contains("第二段失败", StringComparison.Ordinal)) first.TrySetResult();
            if (result.Blocks.All(block => !string.IsNullOrEmpty(block.Translation))) second.TrySetResult(result);
        };
        session.Start(); source.Send(Frame(Color.White)); await first.Task.WaitAsync(TimeSpan.FromSeconds(3));
        session.Retry();
        var completed = await second.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal("Close inventory", Assert.Single(provider.LastInput));
        Assert.Equal("打开背包", completed.Blocks[0].Translation); Assert.Equal("关闭背包", completed.Blocks[1].Translation);
    }

    [Fact]
    public void SmallNewTextOnLargeScreenInvalidatesProbe()
    {
        using var bitmap = new Bitmap(2560, 1440);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.White);
        var before = RealtimeImageProbe.Read(bitmap);
        graphics.FillRectangle(Brushes.Black, 1250, 840, 35, 12);
        Assert.False(before.Matches(RealtimeImageProbe.Read(bitmap)));
    }

    [Fact]
    public async Task ValidatedParagraphAppearsWhileAnotherParagraphIsStillRunning()
    {
        var source = new Source(); var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new Provider(async (_, token) =>
        {
            TranslationSegmentProgress.Report("Open inventory", "打开背包");
            await finish.Task.WaitAsync(token);
            return new(true, ["打开背包", "关闭背包"], null);
        });
        await using var session = new RealtimeTranslationSession(source, (_, _) => Task.FromResult(new OcrRecognitionResult(true, "", null)
        { Regions = [new("Open inventory", 0, 0, 100, 12), new("Close inventory", 0, 60, 100, 12)] }), provider, "zh-Hans", new(""));
        var first = new TaskCompletionSource<RealtimeTranslationUpdate>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Updated += result => first.TrySetResult(result);
        session.Start(); source.Send(Frame(Color.White));
        var result = await first.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal("打开背包", result.Blocks[0].Translation);
        Assert.Empty(result.Blocks[1].Translation);
        finish.TrySetResult();
    }

    [Fact]
    public async Task ProviderFailureKeepsValidatedParagraphsAndCompletesWithAnError()
    {
        var source = new Source();
        var provider = new Provider((_, _) =>
        {
            TranslationSegmentProgress.Report("Open inventory", "打开背包");
            throw new InvalidOperationException("Test provider failed after its first paragraph");
        });
        await using var session = new RealtimeTranslationSession(source, (_, _) => Task.FromResult(new OcrRecognitionResult(true, "", null)
        { Regions = [new("Open inventory", 0, 0, 100, 12), new("Close inventory", 0, 60, 100, 12)] }), provider, "zh-Hans", new(""));
        var completed = new TaskCompletionSource<RealtimeTranslationUpdate>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Updated += result => { if (result.HasFailures) completed.TrySetResult(result); };
        session.Start(); source.Send(Frame(Color.White));
        var result = await completed.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal("打开背包", result.Blocks[0].Translation);
        Assert.Empty(result.Blocks[1].Translation);
        Assert.Contains("Test provider failed", result.Status);
    }

    [Fact]
    public async Task ChangingOneLabelDoesNotCancelAUsefulSlowPageTranslation()
    {
        var source = new Source(); var count = 0; var translated = false; var cancelled = false;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new Provider(async (segments, token) =>
        {
            if (segments.Contains("Open inventory"))
            {
                started.TrySetResult();
                try { await finish.Task.WaitAsync(token); translated = true; }
                catch (OperationCanceledException) { cancelled = true; throw; }
            }
            return new(true, segments.Select(s => s == "Open inventory" ? "打开背包" : "计数").ToArray(), null);
        });
        await using var session = new RealtimeTranslationSession(source, (_, _) =>
        {
            var number = Interlocked.Increment(ref count);
            return Task.FromResult(new OcrRecognitionResult(true, "", null)
            { Regions = [new("Open inventory", 0, 0, 100, 12), new("Count " + number, 0, 60, 100, 12)] });
        }, provider, "zh-Hans", new(""));
        var result = new TaskCompletionSource<RealtimeTranslationUpdate>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Updated += update => { if (update.Blocks[0].Translation == "打开背包") result.TrySetResult(update); };
        session.Start(); source.Send(Frame(Color.White)); await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        source.Send(Frame(Color.Black)); await Until(() => count >= 2);
        finish.TrySetResult(); await result.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(translated); Assert.False(cancelled);
    }

    [Fact]
    public void OffscreenSettingsFollowThemeAndHaveAllControls()
    {
        WpfTestHost.Invoke(() =>
        {
            var settings = new RealtimeTranslationSettings();
            var panel = new RealtimeTranslationSettingsPanel(() => settings, value => settings = value, () => "zh-Hans");
            panel.Measure(new System.Windows.Size(420, 1000)); panel.Arrange(new System.Windows.Rect(0, 0, 420, panel.DesiredSize.Height));
            Assert.True(panel.DesiredSize.Height > 300);
            Assert.Single(panel.Children.OfType<System.Windows.Controls.ComboBox>());
            Assert.Single(panel.Children.OfType<System.Windows.Controls.TextBox>());
        });
    }

    private static RealtimeTranslationSession Session(Source source, Provider provider,
        Func<CapturedImage, CancellationToken, Task<OcrRecognitionResult>> recognize) => new(source, recognize, provider, "zh-Hans", new(""));

    [Theory]
    [InlineData("")]
    [InlineData("这是中文正文")]
    public async Task NoTranslationNeededKeepsListeningAndReportsWhy(string text)
    {
        var source = new Source(); var provider = new Provider();
        await using var session = Session(source, provider, (_, _) => Task.FromResult(
            text.Length == 0 ? new OcrRecognitionResult(true, "", null) : Ocr(text)));
        var ready = new TaskCompletionSource<RealtimeTranslationUpdate>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Updated += value => ready.TrySetResult(value);
        session.Start(); source.Send(Frame(Color.White));
        var output = await ready.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Contains(text.Length == 0 ? "未识别到文字" : "已是目标语言", output.Status);
        Assert.False(session.Completion.IsCompleted);
        Assert.Equal(0, provider.Calls);
    }
    internal static OcrRecognitionResult Ocr(string text) => new(true, text, null) { Regions = [new(text, 0, 0, 100, 20)] };
    internal static RealtimeFrame Frame(Color color)
    {
        var bitmap = new Bitmap(160, 100);
        using (var graphics = Graphics.FromImage(bitmap)) graphics.Clear(color);
        return new(new CapturedImage(bitmap), new(0, 0, 160, 100));
    }
    private static async Task Until(Func<bool> condition)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        while (!condition()) await Task.Delay(20, deadline.Token);
    }
    internal sealed class Source : IRealtimeCaptureSource
    {
        private readonly Channel<RealtimeFrame?> _frames = Channel.CreateUnbounded<RealtimeFrame?>();
        internal bool Disposed;
        internal int Captures;
        internal void Send(RealtimeFrame? frame) => _frames.Writer.TryWrite(frame);
        public async Task<RealtimeFrame?> CaptureAsync(CancellationToken token)
        { var frame = await _frames.Reader.ReadAsync(token); Interlocked.Increment(ref Captures); return frame; }
        public void Dispose() { Disposed = true; while (_frames.Reader.TryRead(out var frame)) frame?.Dispose(); _frames.Writer.TryComplete(); }
    }
    private sealed class Provider(Func<IReadOnlyList<string>, CancellationToken, Task<TranslationSegmentsResult>>? translate = null) : ITranslationProvider
    {
        internal int Calls;
        internal IReadOnlyList<string> LastInput = [];
        public string Id => "test";
        public Task<TranslationResult> TranslateAsync(string text, string sourceLanguage, string targetLanguage, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TranslationSegmentsResult> TranslateSegmentsAsync(IReadOnlyList<string> segments, string sourceLanguage, string targetLanguage, CancellationToken cancellationToken = default)
        { Interlocked.Increment(ref Calls); LastInput = segments; return translate?.Invoke(segments, cancellationToken) ?? Task.FromResult(new TranslationSegmentsResult(true, segments.Select(_ => "打开背包").ToArray(), null)); }
    }
}
