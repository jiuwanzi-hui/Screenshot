using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Runtime.InteropServices;
using Screenshot.App.Capture;
using Screenshot.App.Core;
using Screenshot.App.Text;
using Screenshot.App.Text.Realtime;
using Xunit.Abstractions;
using Color = System.Drawing.Color;

namespace Screenshot.App.Tests;

public sealed class RealtimeTranslationNativeTests(ITestOutputHelper output)
{
    [DesktopFact]
    public async Task ContinuousNativeAnimationKeepsStaticTextAndReadableMovingTranslation()
    {
        Window? sample = null;
        RealtimeOverlayWindow? overlay = null;
        System.Windows.Threading.DispatcherTimer? animation = null;
        nint handle = 0;
        ScreenRegion region = default;
        var frames = 0;
        WpfTestHost.Invoke(() =>
        {
            var canvas = new System.Windows.Controls.Canvas();
            var stationary = new System.Windows.Controls.TextBlock { Text = "Mana", FontSize = 26,
                Foreground = System.Windows.Media.Brushes.Black };
            var moving = new System.Windows.Controls.TextBlock { Text = "Health", FontSize = 26,
                Foreground = System.Windows.Media.Brushes.Black };
            System.Windows.Controls.Canvas.SetLeft(stationary, 24); System.Windows.Controls.Canvas.SetTop(stationary, 24);
            System.Windows.Controls.Canvas.SetLeft(moving, 24); System.Windows.Controls.Canvas.SetTop(moving, 95);
            canvas.Children.Add(stationary); canvas.Children.Add(moving);
            sample = new Window { Width = 430, Height = 220, Left = 160, Top = 140,
                WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false, Topmost = true,
                Background = System.Windows.Media.Brushes.WhiteSmoke, Content = canvas };
            sample.Show(); sample.UpdateLayout(); handle = new WindowInteropHelper(sample).Handle;
            region = RealtimeWindowTarget.Bounds(handle);
            overlay = new();
            animation = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(90) };
            animation.Tick += (_, _) => System.Windows.Controls.Canvas.SetLeft(moving, 24 + Interlocked.Increment(ref frames) * 9 % 220);
            animation.Start();
        });
        await using var session = new RealtimeTranslationSession(new RealtimeCaptureSource(handle, region),
            (image, token) => OcrService.RecognizeAsync(image, "zh-Hans", token),
            new NeverCalledProvider(), "zh-Hans", new("Mana=法力值\nHealth=生命值"));
        var outputFrames = new System.Collections.Concurrent.ConcurrentQueue<RealtimeTranslationUpdate>();
        var invalidations = 0;
        session.Invalidated += () => Interlocked.Increment(ref invalidations);
        session.Updated += update =>
        {
            outputFrames.Enqueue(update);
            WpfTestHost.Invoke(() => overlay!.Present(update, new(), false));
        };
        try
        {
            session.Start();
            var watch = Stopwatch.StartNew();
            while (!outputFrames.Any(update => update.ReadingBlocks.Any(block => block.Translation == "生命值")) && watch.Elapsed < TimeSpan.FromSeconds(10))
                await Task.Delay(60);
            Assert.Contains(outputFrames, update => update.ReadingBlocks.Any(block => block.Translation == "生命值"));
            var start = outputFrames.Count;
            await Task.Delay(1800);
            Assert.True(frames >= 20);
            Assert.Contains(outputFrames.Skip(start), update => update.Blocks.Any(block => block.Translation == "法力值"));
            Assert.All(outputFrames.Where(update => update.ReadingBlocks.Count > 0), update =>
            { Assert.False(update.IsReadingSnapshot); Assert.Contains(update.Blocks, block => block.Translation == "法力值"); });
            Assert.Equal(0, invalidations);
            output.WriteLine($"Continuous native animation: {frames} movements, {outputFrames.Count} updates; static and moving Chinese visible, no blank invalidation.");
        }
        finally
        {
            WpfTestHost.Invoke(() => animation?.Stop());
            await session.DisposeAsync();
            WpfTestHost.Invoke(() => { overlay?.Close(); sample?.Close(); });
        }
    }

    private sealed class NeverCalledProvider : ITranslationProvider
    {
        public string Id => "glossary-only";
        public Task<TranslationResult> TranslateAsync(string text, string sourceLanguage, string targetLanguage, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Native animation test expects exact glossary matches.");
        public Task<TranslationSegmentsResult> TranslateSegmentsAsync(IReadOnlyList<string> segments, string sourceLanguage, string targetLanguage, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Native animation test expects exact glossary matches.");
    }

    [DesktopFact]
    public async Task WarmRealtimeSelectionCompletesOnMouseReleaseAndTranslatesExactlyThatPhysicalRegion()
    {
        Window? sample = null;
        RealtimeTranslationController? controller = null;
        using var http = new System.Net.Http.HttpClient();
        Task? startup = null;
        ScreenRegion expected = default;
        _ = GetCursorPos(out var savedCursor);
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        T? Field<T>(string name) where T : class => (T?)typeof(RealtimeTranslationController).GetField(name, flags)!.GetValue(controller);
        try
        {
            WpfTestHost.Invoke(() =>
            {
                sample = new Window { Width = 340, Height = 150, WindowStyle = WindowStyle.None,
                    Left = 180, Top = 180, ShowInTaskbar = false, ShowActivated = true, Topmost = true,
                    Background = System.Windows.Media.Brushes.White,
                    Content = new System.Windows.Controls.TextBlock { Text = "Mana", FontSize = 30,
                        Foreground = System.Windows.Media.Brushes.Black, Margin = new Thickness(24) } };
                sample.Show(); sample.UpdateLayout();
                CaptureTargetWindowResolver.AllowContentWindow(new WindowInteropHelper(sample).Handle);
                var bounds = RealtimeWindowTarget.Bounds(new WindowInteropHelper(sample).Handle);
                expected = new(bounds.X + 4, bounds.Y + 4, bounds.Width - 8, bounds.Height - 8);
                var settings = AppSettings.CreateDefault() with { OcrEngine = OcrEngineMode.Windows,
                    OcrLanguageTag = "zh-Hans", TranslationTargetLanguage = "zh-Hans", CaptureAspectRatio = CaptureAspectRatio.Ratio1x1,
                    RealtimeTranslation = new() { Glossary = "Mana=法力值" } };
                controller = new(() => settings, http);
                startup = controller.StartAsync(0);
            });
            var watch = Stopwatch.StartNew();
            ResidentSelectionWindow? selector = null;
            while (selector is null && watch.Elapsed < TimeSpan.FromSeconds(8))
            {
                WpfTestHost.Invoke(() => selector = Field<ResidentSelectionWindow>("_selector"));
                if (selector is null) await Task.Delay(30);
            }
            Assert.NotNull(selector);
            WpfTestHost.Invoke(() =>
            {
                Assert.DoesNotContain(System.Windows.Application.Current.Windows.Cast<Window>(), window => window is CaptureOverlayWindow);
                // Real physical pointer coordinates pass through the native mouse handlers.
                _ = SetCursorPos(expected.X, expected.Y);
                _ = SendMessage(selector.Handle, 0x201, 1, 0);
                _ = SetCursorPos(expected.X + expected.Width, expected.Y + expected.Height);
                _ = SendMessage(selector.Handle, 0x200, 1, 0);
                _ = SendMessage(selector.Handle, 0x202, 0, 0);
                Assert.True(selector.Selection.IsCompletedSuccessfully);
                Assert.Equal(expected, selector.Selection.Result?.Region);
            });
            await startup!.WaitAsync(TimeSpan.FromSeconds(8));
            RealtimeTranslationUpdate? translated = null;
            while (watch.Elapsed < TimeSpan.FromSeconds(15))
            {
                WpfTestHost.Invoke(() => translated = Field<RealtimeTranslationUpdate>("_output"));
                if (translated?.Blocks.Any(block => block.Translation.Contains("法力值", StringComparison.Ordinal)) == true) break;
                await Task.Delay(50);
            }
            WpfTestHost.Invoke(() => output.WriteLine("Controller status: " + Field<string>("_lastStatus")));
            if (translated is null)
            {
                using var capture = new RealtimeCaptureSource(0, expected);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                RealtimeFrame? frame = null;
                while (frame is null) { frame = await capture.CaptureAsync(timeout.Token); if (frame is null) await Task.Delay(50, timeout.Token); }
                using (frame)
                {
                    var ocr = await OcrService.RecognizeAsync(frame.Image, "zh-Hans", timeout.Token);
                    output.WriteLine($"ROI {expected}; direct OCR: {ocr.Text}; {ocr.ErrorMessage}; {capture.Notice}");
                }
            }
            Assert.NotNull(translated);
            Assert.Equal(expected, translated.Bounds);
            Assert.Equal(expected.Width, translated.PixelWidth); Assert.Equal(expected.Height, translated.PixelHeight);
            Assert.Contains(translated.Blocks, block => block.Translation.Contains("法力值", StringComparison.Ordinal));
            WpfTestHost.Invoke(() =>
            {
                var overlay = Field<RealtimeOverlayWindow>("_overlay");
                Assert.NotNull(overlay);
                Assert.Equal(expected, RealtimeWindowTarget.Bounds(new WindowInteropHelper(overlay).Handle));
                AssertVisibleTranslation(overlay, "法力值");
            });
            output.WriteLine($"Physical selection, captured pixels and overlay agree: {expected}; OCR → 法力值: {watch.ElapsedMilliseconds} ms.");
        }
        finally
        {
            WpfTestHost.Invoke(() => controller?.Close());
            if (controller is not null) await controller.Shutdown.WaitAsync(TimeSpan.FromSeconds(10));
            _ = SetCursorPos(savedCursor.X, savedCursor.Y);
            WpfTestHost.Invoke(() => sample?.Close());
        }
    }

    internal static void AssertVisibleTranslation(RealtimeOverlayWindow overlay, string text)
    {
        overlay.UpdateLayout();
        var root = Assert.IsType<System.Windows.Controls.Grid>(overlay.Content);
        var canvas = Assert.IsType<System.Windows.Controls.Canvas>(root.Children[0]);
        var translated = Assert.Single(canvas.Children.OfType<System.Windows.Controls.Border>()
            .Where(b => b.Child is System.Windows.Controls.TextBlock t && t.Text.Contains(text, StringComparison.Ordinal)));
        Assert.True(translated.ActualWidth > 24 && translated.ActualHeight > 15);
        Assert.True(root.ActualWidth > 40 && root.ActualHeight > 30);
        var rect = translated.TransformToAncestor(root).TransformBounds(new Rect(translated.RenderSize));
        Assert.True(new Rect(root.RenderSize).IntersectsWith(rect));
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(root);
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Round(root.ActualWidth * dpi.DpiScaleX),
            (int)Math.Round(root.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(root);
        if (Environment.GetEnvironmentVariable("SNAPCUT_TEST_RENDER_DIR") is { Length: > 0 } directory)
        {
            Directory.CreateDirectory(directory);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(directory, "realtime-overlay.png"));
            encoder.Save(file);
        }
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        // A physical HWND in the right place is insufficient: verify drawn frame
        // on all four sides and opaque translated content inside that frame.
        byte Alpha(int x, int y) => pixels[(y * bitmap.PixelWidth + x) * 4 + 3];
        Assert.True(Alpha(0, bitmap.PixelHeight / 2) > 0);
        Assert.True(Alpha(bitmap.PixelWidth - 1, bitmap.PixelHeight / 2) > 0);
        Assert.True(Alpha(bitmap.PixelWidth / 2, 0) > 0);
        Assert.True(Alpha(bitmap.PixelWidth / 2, bitmap.PixelHeight - 1) > 0);
        Assert.True(Alpha(Math.Clamp((int)((rect.X + rect.Width / 2) * dpi.DpiScaleX), 0, bitmap.PixelWidth - 1),
            Math.Clamp((int)((rect.Y + rect.Height / 2) * dpi.DpiScaleY), 0, bitmap.PixelHeight - 1)) > 0);
    }

    [DesktopTheory]
    [InlineData(AppTheme.GlassInk)]
    [InlineData(AppTheme.GlassMist)]
    [InlineData(AppTheme.AuroraMist)]
    public void OverlayKeepsRegionAndProgressWhenTranslationsAreInvalidated(AppTheme theme)
    {
        WpfTestHost.Invoke(() =>
        {
            var overlay = new RealtimeOverlayWindow();
            try
            {
                Screenshot.App.Infrastructure.AppThemeManager.ApplySettingsPalette(overlay.Resources, theme);
                var region = new ScreenRegion(230, 180, 401, 197);
                var options = new RealtimeTranslationSettings();
                overlay.Present(new(region, region.Width, region.Height,
                    [new(new OcrTextRegion("Mana", 20, 30, 100, 30), "法力值")], "译文已更新"), options, false);
                AssertVisibleTranslation(overlay, "法力值");
                overlay.PresentStatus(region, "正在翻译剩余文字…", options);
                AssertVisibleTranslation(overlay, "法力值");
                overlay.ClearTranslation();
                overlay.PresentStatus(region, "画面已变化，正在更新译文…", options);
                overlay.UpdateLayout();
                var surface = Assert.IsType<System.Windows.Controls.Grid>(overlay.Content);
                Assert.Empty(Assert.IsType<System.Windows.Controls.Canvas>(surface.Children[0]).Children);
                Assert.True(Assert.IsType<System.Windows.Controls.Border>(surface.Children[1]).ActualWidth > 40);
                Assert.Equal(2, surface.Children.Count);
                var status = overlay.StatusBadge;
                Assert.True(status.IsVisible && status.Width <= 35 && status.Height <= 23);
                Assert.Contains("画面已变化", status.Details);
                Assert.Equal(region, RealtimeWindowTarget.Bounds(new WindowInteropHelper(overlay).Handle));
            }
            finally { overlay.Close(); }
        });
    }

    [DesktopFact]
    public async Task FailedRecognitionKeepsTheCauseVisibleAfterItsSessionIsReleased()
    {
        using var http = new System.Net.Http.HttpClient();
        RealtimeTranslationController? controller = null;
        Task? start = null;
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        object? Field(string name) => typeof(RealtimeTranslationController).GetField(name, flags)!.GetValue(controller);
        try
        {
            WpfTestHost.Invoke(() =>
            {
                var settings = AppSettings.CreateDefault() with { OcrEngine = OcrEngineMode.Windows, OcrLanguageTag = "" };
                controller = new(() => settings, http);
                start = controller.StartAsync(0, new ScreenRegion(240, 200, 350, 160));
            });
            await start!;
            var released = false;
            var watch = Stopwatch.StartNew();
            while (!released && watch.Elapsed < TimeSpan.FromSeconds(10))
            {
                WpfTestHost.Invoke(() => released = Field("_session") is null);
                if (!released) await Task.Delay(40);
            }
            Assert.True(released);
            WpfTestHost.Invoke(() =>
            {
                var overlay = Assert.IsType<RealtimeOverlayWindow>(Field("_overlay"));
                Assert.True(overlay.IsVisible);
                overlay.UpdateLayout();
                Assert.Contains("请在设置中指定识别语言", overlay.StatusBadge.Details);
            });
        }
        finally
        {
            WpfTestHost.Invoke(() => controller?.Close());
            if (controller is not null) await controller.Shutdown.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [NativeRealtimeFact]
    public async Task SuppliedEnglishPageProducesChineseWithTheSelectedOfflineModel()
    {
        var sample = Environment.GetEnvironmentVariable("SNAPCUT_REALTIME_PAGE_SAMPLE");
        if (!File.Exists(sample)) return;
        var modelRoot = Environment.GetEnvironmentVariable("SNAPCUT_REALTIME_MODEL_DIRECTORY")!;
        using var ocrManager = new HighQualityOcrModelManager(Path.Combine(modelRoot, Screenshot.App.Core.AppMetadata.HighQualityOcrModelDirectoryName));
        using var manager = new LocalLargeTranslationModelManager(Path.Combine(modelRoot, "TranslateGemma-4B-Q4-K-M"));
        using var backup = new OfflineTranslationModelManager(modelRoot);
        var source = new RealtimeTranslationTests.Source();
        await using var session = new RealtimeTranslationSession(source, async (image, token) =>
        {
            var result = await HighQualityOcrService.RecognizeAsync(image, ocrManager, token);
            output.WriteLine($"OCR: {result.Regions.Count} regions; {result.Text}");
            return result;
        }, new OrderedTranslationProvider([new LocalLargeModelTranslationProvider(manager, new OfflineTranslationProvider(backup))]), "zh-Hans", new(""));
        var ready = new TaskCompletionSource<RealtimeTranslationUpdate>(TaskCreationOptions.RunContinuationsAsynchronously);
        long firstMilliseconds = 0;
        var watch = Stopwatch.StartNew();
        session.Updated += value =>
        {
            if (firstMilliseconds == 0) firstMilliseconds = watch.ElapsedMilliseconds;
            if (value.Status != "正在翻译剩余文字…") ready.TrySetResult(value);
        };
        session.StatusChanged += message => output.WriteLine(message);
        var bitmap = new Bitmap(sample!);
        session.Start();
        source.Send(new(new CapturedImage(bitmap), new(0, 0, bitmap.Width, bitmap.Height)));
        var translated = await ready.Task.WaitAsync(TimeSpan.FromSeconds(150));
        output.WriteLine($"Page first translation: {firstMilliseconds} ms; complete: {watch.ElapsedMilliseconds} ms\n{translated.Status}");
        foreach (var block in translated.Blocks) output.WriteLine(block.Region.Text + " => " + block.Translation);
        var content = string.Join('\n', translated.Blocks.Select(b => b.Translation));
        Assert.Contains("社区", content);
        Assert.Contains("知识", content);
        Assert.Contains(translated.Blocks, b => b.Region.Text == "Communities that last" && !string.IsNullOrWhiteSpace(b.Translation));
        Assert.Contains(translated.Blocks, b => b.Region.Text == "Learning for the future" && b.Translation.Contains("未来", StringComparison.Ordinal));
        Assert.Contains(translated.Blocks, b => b.Region.Text.StartsWith("Tech moves fast.", StringComparison.Ordinal) &&
            b.Region.Text.EndsWith("of modern businesses.", StringComparison.Ordinal) && b.Translation.Contains("2008", StringComparison.Ordinal));
        Assert.Contains(translated.Blocks, b => b.Region.Text.StartsWith("75% of developers still", StringComparison.Ordinal) && b.Translation.Contains("75", StringComparison.Ordinal));
        Assert.Contains(translated.Blocks, b => b.Region.Text.Contains("future", StringComparison.OrdinalIgnoreCase) && b.Translation.Contains("未来", StringComparison.Ordinal));
    }

    [NativeRealtimeDesktopFact]
    public async Task LiveDesktopPaddleAndSelectedModelActuallyPaintChineseAndRefreshChangedText()
    {
        var modelRoot = Environment.GetEnvironmentVariable("SNAPCUT_REALTIME_MODEL_DIRECTORY")!;
        using var ocr = new HighQualityOcrModelManager(Path.Combine(modelRoot, Screenshot.App.Core.AppMetadata.HighQualityOcrModelDirectoryName));
        using var model = new LocalLargeTranslationModelManager(Path.Combine(modelRoot, "TranslateGemma-4B-Q4-K-M"));
        using var backup = new OfflineTranslationModelManager(modelRoot);
        Window? sample = null;
        RealtimeOverlayWindow? overlay = null;
        nint sampleHandle = 0;
        ScreenRegion region = default;
        var updates = System.Threading.Channels.Channel.CreateUnbounded<RealtimeTranslationUpdate>();
        WpfTestHost.Invoke(() =>
        {
            sample = new Window { Width = 440, Height = 180, Left = 200, Top = 160,
                ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None, Topmost = true,
                Background = System.Windows.Media.Brushes.White,
                Content = new System.Windows.Controls.TextBlock { Text = "Learning for the future", FontSize = 26,
                    Foreground = System.Windows.Media.Brushes.Black, Margin = new Thickness(24) } };
            sample.Show(); sample.UpdateLayout();
            sampleHandle = new WindowInteropHelper(sample).Handle;
            var bounds = RealtimeWindowTarget.Bounds(sampleHandle);
            region = new(bounds.X + 4, bounds.Y + 4, bounds.Width - 8, bounds.Height - 8);
            overlay = new();
            Screenshot.App.Infrastructure.AppThemeManager.ApplySettingsPalette(overlay.Resources, AppTheme.GlassInk);
            overlay.PresentStatus(region, "正在识别选区；再次按实时翻译快捷键或使用托盘菜单停止", new());
        });
        await using var session = new RealtimeTranslationSession(new RealtimeCaptureSource(sampleHandle, region),
            (image, token) => HighQualityOcrService.RecognizeAsync(image, ocr, token),
            new OrderedTranslationProvider([new LocalLargeModelTranslationProvider(model, new OfflineTranslationProvider(backup))]),
            "zh-Hans", new(""));
        session.StatusChanged += status => WpfTestHost.Invoke(() => overlay!.PresentStatus(region, status, new()));
        session.Invalidated += () => WpfTestHost.Invoke(() => overlay!.ClearTranslation());
        session.Updated += update => WpfTestHost.Invoke(() =>
        {
            overlay!.Present(update, new(), false);
            updates.Writer.TryWrite(update);
        });
        var watch = Stopwatch.StartNew();
        try
        {
            session.Start();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            async Task WaitFor(string word)
            {
                while (true)
                {
                    var update = await updates.Reader.ReadAsync(timeout.Token);
                    output.WriteLine(string.Join(" | ", update.Blocks.Select(b => b.Region.Text + " => " + b.Translation)));
                    if (!update.Blocks.Any(b => b.Translation.Contains(word, StringComparison.Ordinal)) ||
                        update.Status is "正在翻译剩余文字…" or "已显示缓存译文，正在翻译新文字…") continue;
                    WpfTestHost.Invoke(() => AssertVisibleTranslation(overlay!, word));
                    break;
                }
            }
            await WaitFor("未来");
            output.WriteLine($"Visible live PP-OCR → TranslateGemma translation: {watch.ElapsedMilliseconds} ms.");
            WpfTestHost.Invoke(() => ((System.Windows.Controls.TextBlock)sample!.Content).Text = "Knowledge is power");
            watch.Restart();
            await WaitFor("知识");
            output.WriteLine($"Changed source translated and painted: {watch.ElapsedMilliseconds} ms.");
        }
        finally
        {
            await session.DisposeAsync();
            WpfTestHost.Invoke(() => { overlay?.Close(); sample?.Close(); });
        }
    }

    private sealed class NativeRealtimeDesktopFactAttribute : FactAttribute
    {
        public NativeRealtimeDesktopFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("SNAPCUT_RUN_DESKTOP_TESTS") != "1" ||
                !Directory.Exists(Environment.GetEnvironmentVariable("SNAPCUT_REALTIME_MODEL_DIRECTORY")))
                Skip = "Enable desktop tests and set SNAPCUT_REALTIME_MODEL_DIRECTORY to test installed models.";
        }
    }

    [DesktopFact]
    public async Task VisibleTranslationIsCapturedByDesktopButNeverFedBackIntoWindowOcr()
    {
        Window? sample = null;
        RealtimeOverlayWindow? overlay = null;
        nint handle = 0;
        ScreenRegion region = default;
        try
        {
            WpfTestHost.Invoke(() =>
            {
                sample = new Window { Width = 460, Height = 180, Left = 200, Top = 160,
                    WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false, Topmost = true,
                    Background = System.Windows.Media.Brushes.White,
                    Content = new System.Windows.Controls.TextBlock { Text = "Knowledge is power", FontSize = 26,
                        Foreground = System.Windows.Media.Brushes.Black, Margin = new Thickness(24) } };
                sample.Show(); sample.UpdateLayout();
                handle = new WindowInteropHelper(sample).Handle;
                CaptureTargetWindowResolver.AllowContentWindow(handle);
                var bounds = RealtimeWindowTarget.Bounds(handle);
                region = new(bounds.X + 4, bounds.Y + 4, bounds.Width - 8, bounds.Height - 8);
                Assert.Equal(handle, RealtimeWindowTarget.FromRegion(region));
                overlay = new();
                overlay.Present(new(region, region.Width, region.Height,
                    [new(new OcrTextRegion("Knowledge is power", 15, 20, 460, 60), "知识就是力量")], "译文已更新"),
                    new() { FontSize = 26, BackgroundOpacity = 1 }, false);
            });
            await Task.Delay(300);
            using var originalSource = new RealtimeCaptureSource(handle, region);
            using var remoteView = new RealtimeCaptureSource(0, region);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            async Task<RealtimeFrame> Read(IRealtimeCaptureSource source)
            {
                while (true)
                {
                    if (await source.CaptureAsync(timeout.Token) is { } frame) return frame;
                    await Task.Delay(50, timeout.Token);
                }
            }
            using (var original = await Read(originalSource))
            using (var remote = await Read(remoteView))
            {
                var originalOcr = await OcrService.RecognizeAsync(original.Image, "zh-Hans", timeout.Token);
                var remoteOcr = await OcrService.RecognizeAsync(remote.Image, "zh-Hans", timeout.Token);
                output.WriteLine($"Underlying HWND OCR: {originalOcr.Text}; remote desktop OCR: {remoteOcr.Text}");
                Assert.Contains("Knowledge", originalOcr.Text, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("知识", originalOcr.Text);
                Assert.Contains("知识", remoteOcr.Text.Replace(" ", "", StringComparison.Ordinal));
                // Verify physical output, not only the WPF visual tree. All
                // four selected-region edges survive desktop capture.
                foreach (var point in new[] { new System.Drawing.Point(0, region.Height / 2),
                    new System.Drawing.Point(region.Width - 1, region.Height / 2),
                    new System.Drawing.Point(region.Width / 2, 0), new System.Drawing.Point(region.Width / 2, region.Height - 1) })
                    Assert.NotEqual(original.Image.Bitmap.GetPixel(point.X, point.Y), remote.Image.Bitmap.GetPixel(point.X, point.Y));
            }
            WpfTestHost.Invoke(() => ((System.Windows.Controls.TextBlock)sample!.Content).Text = "Learning for the future");
            var updated = false;
            for (var i = 0; i < 30 && !updated; i++)
            {
                using var frame = await Read(originalSource);
                var result = await OcrService.RecognizeAsync(frame.Image, "zh-Hans", timeout.Token);
                updated = result.Text.Contains("future", StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("知识", result.Text);
                if (!updated) await Task.Delay(100, timeout.Token);
            }
            Assert.True(updated);
        }
        finally { WpfTestHost.Invoke(() => { overlay?.Close(); sample?.Close(); }); }
    }

    [DesktopFact]
    public void TranslationOverlayDoesNotActivateOrInterceptClicksAndRemainsVisibleToRemoteCapture()
    {
        WpfTestHost.Invoke(() =>
        {
            var foreground = GetForegroundWindow();
            var overlay = new RealtimeOverlayWindow();
            try
            {
                var handle = new WindowInteropHelper(overlay).EnsureHandle();
                var style = GetWindowLongPtr(handle, -20).ToInt64();
                Assert.NotEqual(0, style & 0x08000000);
                Assert.NotEqual(0, style & 0x20);
                Assert.True(GetWindowDisplayAffinity(handle, out var affinity)); Assert.Equal(0u, affinity);
                Assert.Equal(new nint(1), GetProp(handle, "NonRudeHWND"));
                Assert.Equal(foreground, GetForegroundWindow());
                var region = new ScreenRegion(230, 180, 401, 197);
                overlay.PresentStatus(region, "实际使用：TranslateGemma 4B\n翻译失败，可点重试：测试原因", new());
                var badge = overlay.StatusBadge;
                var badgeHandle = new WindowInteropHelper(badge).Handle;
                Assert.NotEqual(0, GetWindowLongPtr(badgeHandle, -20).ToInt64() & 0x08000000);
                Assert.Equal(0, GetWindowLongPtr(badgeHandle, -20).ToInt64() & 0x20);
                Assert.True(GetWindowDisplayAffinity(badgeHandle, out var badgeAffinity)); Assert.Equal(0u, badgeAffinity);
                Assert.True(RealtimeWindowTarget.Bounds(badgeHandle).Y >= region.Y + region.Height);
                var badgeContent = Assert.IsType<System.Windows.Controls.Border>(badge.Content);
                var tip = Assert.IsType<System.Windows.Controls.ToolTip>(badgeContent.ToolTip);
                Assert.False(tip.IsOpen);
                badgeContent.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0,
                    System.Windows.Input.MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
                Assert.True(tip.IsOpen);
                Assert.Contains("TranslateGemma 4B", Assert.IsType<System.Windows.Controls.TextBlock>(tip.Content).Text);
                Assert.Contains("测试原因", Assert.IsType<System.Windows.Controls.TextBlock>(tip.Content).Text);
                Assert.Equal(foreground, GetForegroundWindow());
                overlay.Close();
                Assert.False(tip.IsOpen); Assert.False(badge.IsVisible);
            }
            finally { overlay.Close(); }
        });
    }

    [NativeRealtimeFact]
    public async Task ActualOcrToOfflineTranslationPreservesGameTermsAndReusesStableText()
    {
        var modelRoot = Environment.GetEnvironmentVariable("SNAPCUT_REALTIME_MODEL_DIRECTORY")!;
        using var ocrManager = new HighQualityOcrModelManager(Path.Combine(modelRoot, Screenshot.App.Core.AppMetadata.HighQualityOcrModelDirectoryName));
        using var translationManager = new OfflineTranslationModelManager(modelRoot);
        var provider = new OfflineTranslationProvider(translationManager);
        var source = new RealtimeTranslationTests.Source();
        await using var session = new RealtimeTranslationSession(source,
            (image, token) => HighQualityOcrService.RecognizeAsync(image, ocrManager, token),
            provider, "zh-Hans", new("Mana=法力值\nplayer=玩家\nshield=护盾"));
        var results = System.Threading.Channels.Channel.CreateUnbounded<RealtimeTranslationUpdate>();
        session.Updated += result => { if (result.Blocks.All(block => !string.IsNullOrEmpty(block.Translation))) results.Writer.TryWrite(result); };
        session.StatusChanged += message => output.WriteLine(message);
        var watch = Stopwatch.StartNew();
        session.Start(); source.Send(CreateFrame(Color.White));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var first = await results.Reader.ReadAsync(timeout.Token);
        var firstMilliseconds = watch.ElapsedMilliseconds;
        var firstText = string.Join('\n', first.Blocks.Select(block => block.Translation));
        output.WriteLine($"First OCR + offline translation: {firstMilliseconds} ms.\n{firstText}\n{first.Status}");
        Assert.Contains("法力值", firstText);
        Assert.Contains("玩家", firstText);
        Assert.Contains("护盾", firstText);
        watch.Restart(); source.Send(CreateFrame(Color.FromArgb(226, 233, 238)));
        var second = await results.Reader.ReadAsync(timeout.Token);
        output.WriteLine($"Continuous OCR + cached translation: {watch.ElapsedMilliseconds} ms.");
        Assert.Equal(firstText, string.Join('\n', second.Blocks.Select(block => block.Translation)));
        await session.DisposeAsync();
        Assert.True(source.Disposed);
        await Task.Delay(TimeSpan.FromSeconds(12), timeout.Token);
        var workers = Process.GetProcessesByName("SnapCut");
        try { output.WriteLine($"SnapCut processes after idle: {workers.Length}"); Assert.Empty(workers); }
        finally { foreach (var process in workers) process.Dispose(); }
    }

    [NativeRealtimeFact]
    public async Task ActualGenerativeModelsPreserveProtectedTermsWithOcrInput()
    {
        var modelRoot = Environment.GetEnvironmentVariable("SNAPCUT_REALTIME_MODEL_DIRECTORY")!;
        using var ocrManager = new HighQualityOcrModelManager(Path.Combine(modelRoot, Screenshot.App.Core.AppMetadata.HighQualityOcrModelDirectoryName));
        foreach (var lightweight in new[] { true, false })
        {
            using var manager = new LocalLargeTranslationModelManager(Path.Combine(modelRoot,
                lightweight ? "Hy-MT2-1.8B" : "TranslateGemma-4B-Q4-K-M"), lightweight: lightweight);
            Assert.True(manager.GetStatus().IsInstalled, manager.DisplayName + " is not installed.");
            var provider = new LocalLargeModelTranslationProvider(manager);
            var source = new RealtimeTranslationTests.Source();
            await using var session = new RealtimeTranslationSession(source,
                (image, token) => HighQualityOcrService.RecognizeAsync(image, ocrManager, token), provider, "zh-Hans",
                new("Mana=法力值\nplayer=玩家\nshield=护盾"));
            var ready = new TaskCompletionSource<RealtimeTranslationUpdate>(TaskCreationOptions.RunContinuationsAsynchronously);
            session.Updated += result =>
            {
                if (!result.Status.StartsWith("已显示缓存", StringComparison.Ordinal) && result.Status != "正在翻译剩余文字…" &&
                    result.Blocks.All(block => !string.IsNullOrWhiteSpace(block.Translation)))
                    ready.TrySetResult(result);
            };
            session.StatusChanged += message => output.WriteLine(manager.DisplayName + ": " + message);
            var watch = Stopwatch.StartNew(); session.Start(); source.Send(CreateFrame(Color.White));
            var first = await ready.Task.WaitAsync(TimeSpan.FromSeconds(90));
            var text = string.Join('\n', first.Blocks.Select(block => block.Translation));
            output.WriteLine($"{manager.DisplayName}: OCR + first translation {watch.ElapsedMilliseconds} ms; {manager.InferenceSession.RuntimeDescription}\n{text}\n{first.Status}");
            Assert.Contains("玩家", text); Assert.Contains("护盾", text); Assert.Contains("法力值", text);
            Assert.DoesNotContain("ZXQTERM", text);
            ready = new TaskCompletionSource<RealtimeTranslationUpdate>(TaskCreationOptions.RunContinuationsAsynchronously);
            watch.Restart(); source.Send(CreateFrame(Color.White, "The player has a shield."));
            var second = await ready.Task.WaitAsync(TimeSpan.FromSeconds(45));
            var secondText = string.Join('\n', second.Blocks.Select(block => block.Translation));
            output.WriteLine($"{manager.DisplayName}: changed text with warm model {watch.ElapsedMilliseconds} ms\n{secondText}");
            Assert.Contains("has a shield", string.Join('\n', second.Blocks.Select(block => block.Region.Text)));
            Assert.Contains("玩家", secondText); Assert.Contains("护盾", secondText);
        }
    }

    [DesktopFact]
    public async Task WindowsGraphicsCaptureReadsItsOwnNeutralTestWindowAndReleases()
    {
        Window? window = null; nint handle = 0;
        WpfTestHost.Invoke(() =>
        {
            window = new Window { Width = 440, Height = 155, Title = "SnapCut 实时翻译采集验证", ShowActivated = false,
                ShowInTaskbar = false, Background = System.Windows.Media.Brushes.White,
                Content = new System.Windows.Controls.TextBlock { Text = "Mana Shield\nThe player needs a shield.",
                    FontSize = 24, Foreground = System.Windows.Media.Brushes.Black, Margin = new Thickness(16) } };
            window.Show(); handle = new WindowInteropHelper(window).Handle;
        });
        try
        {
            using var source = await Task.Run(() => new RealtimeCaptureSource(handle, null));
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            RealtimeFrame? frame = null;
            while (frame is null) { frame = await source.CaptureAsync(deadline.Token); if (frame is null) await Task.Delay(50, deadline.Token); }
            using (frame)
            {
                Assert.Null(source.Notice);
                Assert.True(frame.Image.PixelWidth >= 400); Assert.True(frame.Image.PixelHeight >= 120);
                var result = await OcrService.RecognizeAsync(frame.Image, "zh-Hans", deadline.Token);
                Assert.True(result.IsSuccess, result.ErrorMessage);
                Assert.Contains("Mana", result.Text, StringComparison.OrdinalIgnoreCase);
                output.WriteLine($"WGC: {frame.Image.PixelWidth}x{frame.Image.PixelHeight}; OCR: {result.Text}");
            }
        }
        finally { WpfTestHost.Invoke(() => window?.Close()); }
    }

    private static RealtimeFrame CreateFrame(Color background, string sentence = "The player needs a shield.")
    {
        var bitmap = new Bitmap(650, 180);
        using (var graphics = Graphics.FromImage(bitmap))
        using (var font = new Font("Arial", 24, System.Drawing.FontStyle.Regular, GraphicsUnit.Pixel))
        {
            graphics.Clear(background);
            graphics.DrawString("Mana", font, System.Drawing.Brushes.Black, 24, 20);
            graphics.DrawString(sentence, font, System.Drawing.Brushes.Black, 24, 90);
        }
        return new(new CapturedImage(bitmap), new(0, 0, 650, 180));
    }
    private sealed class NativeRealtimeFactAttribute : FactAttribute
    {
        public NativeRealtimeFactAttribute()
        {
            if (!Directory.Exists(Environment.GetEnvironmentVariable("SNAPCUT_REALTIME_MODEL_DIRECTORY")))
                Skip = "Set SNAPCUT_REALTIME_MODEL_DIRECTORY to use installed OCR and offline translation models.";
        }
    }
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out System.Drawing.Point point);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll", EntryPoint = "SendMessageW")] private static extern nint SendMessage(nint window, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint handle, int index);
    [DllImport("user32.dll")] private static extern bool GetWindowDisplayAffinity(nint handle, out uint affinity);
    [DllImport("user32.dll", EntryPoint = "GetPropW", CharSet = CharSet.Unicode)] private static extern nint GetProp(nint handle, string name);
}
