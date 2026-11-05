using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Screenshot.App.Capture;
using Screenshot.App.Core;
using Screenshot.App.Editor;
using Screenshot.App.Infrastructure;
using Screenshot.App.Pin;
using Screenshot.App.Presentation;
using Screenshot.App.Text;

namespace Screenshot.App.Tests;

public sealed class MemoryLifetimeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnusedEmojiPaletteDoesNotRenderEveryStickerWhenAToolbarIsCreated(bool pinned)
    {
        WpfTestHost.Invoke(() =>
        {
            var owner = new Window();
            _ = new System.Windows.Interop.WindowInteropHelper(owner).EnsureHandle();
            Window toolbar = pinned
                ? new Screenshot.App.Pin.PinnedImageEditorToolbarWindow(owner, AppSettings.CreateDefault())
                : new CaptureOverlayWindow(null, isStartupPrewarm: true, deferInitialScreenCapture: true);
            try
            {
                var panel = (Panel)toolbar.FindName(pinned ? "EmojiPalettePanel" : "InlineEmojiPanel");
                Assert.Empty(panel.Children);
                var update = toolbar.GetType().GetMethod(pinned ? "UpdateEmojiPaletteVisibility" : "UpdateInlineToolOptionPanels", BindingFlags.NonPublic | BindingFlags.Instance)!;
                ((RadioButton)toolbar.FindName(pinned ? "EmojiToolButton" : "InlineEmojiToolButton")).IsChecked = true;
                update.Invoke(toolbar, null);
                Assert.Equal(EmojiStickerCatalog.All.Count, panel.Children.Count);
                var first = panel.Children[0];
                update.Invoke(toolbar, null);
                Assert.Same(first, panel.Children[0]);
                Assert.Equal(EmojiStickerCatalog.All.Count, panel.Children.Count);
            }
            finally { toolbar.Close(); owner.Close(); }
        });
    }

    [Fact]
    public void UnusedGlassTextureIsCollectibleWhileLiveTextureIsShared()
    {
        WeakReference? retired = null;
        WpfTestHost.Invoke(() =>
        {
            var brush = LiquidGlassMaterial.CreateBackground(false, true);
            Assert.True(brush.IsFrozen);
            Assert.Same(brush, LiquidGlassMaterial.CreateBackground(false, true));
            retired = new WeakReference(brush);
        });
        // Let the dispatcher retire the invocation before checking the cache's
        // ownership. Test-only collection does not display a desktop window.
        WpfTestHost.Invoke(() => { });
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Assert.False(retired!.IsAlive);
    }

    [Fact]
    public void IdleEmojiCacheReleasePreservesExistingImagesAndRendersIdenticalPixels()
    {
        WpfTestHost.Invoke(() =>
        {
            var original = (BitmapSource)EmojiStickerRenderer.GetImage("😊", 48);
            var pixels = new byte[48 * 48 * 4];
            original.CopyPixels(pixels, 48 * 4, 0);
            EmojiStickerRenderer.ReleaseIdleCache();
            var retainedPixels = new byte[pixels.Length];
            original.CopyPixels(retainedPixels, 48 * 4, 0);
            Assert.Equal(pixels, retainedPixels);
            var fresh = (BitmapSource)EmojiStickerRenderer.GetImage("😊", 48);
            Assert.NotSame(original, fresh);
            fresh.CopyPixels(retainedPixels, 48 * 4, 0);
            Assert.Equal(pixels, retainedPixels);
            Assert.True(original.IsFrozen && fresh.IsFrozen);
            EmojiStickerRenderer.ReleaseIdleCache();
            var lazy = typeof(EmojiStickerRenderer).GetField("Font", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
            Assert.False((bool)lazy.GetType().GetProperty("IsValueCreated")!.GetValue(lazy)!);
        });
    }

    [Theory]
    [InlineData(PixelFormat.Format24bppRgb)]
    [InlineData(PixelFormat.Format32bppArgb)]
    [InlineData(PixelFormat.Format32bppPArgb)]
    public void PreviewOwnsItsPixelsWithoutAllocatingAFullManagedFrame(PixelFormat format)
    {
        using var warmup = new Bitmap(2, 2);
        _ = CapturedImage.ToBitmapSource(warmup);
        using var bitmap = new Bitmap(1024, 768, format);
        bitmap.SetPixel(4, 3, System.Drawing.Color.FromArgb(255, 19, 71, 133));
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        var preview = CapturedImage.ToBitmapSource(bitmap);
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        bitmap.SetPixel(4, 3, System.Drawing.Color.White);
        bitmap.Dispose();
        var pixel = new byte[4];
        preview.CopyPixels(new Int32Rect(4, 3, 1, 1), pixel, 4, 0);
        Assert.Equal(new byte[] { 133, 71, 19, 255 }, pixel);
        Assert.True(preview.IsFrozen);
        Assert.True(allocated < 256 * 1024, $"Allocated {allocated:N0} managed bytes for preview.");
    }

    [Fact]
    public void DisposedCaptureDetachesPreviewButExistingClipboardCopyRemainsValid()
    {
        using var image = new CapturedImage(new Bitmap(24, 16));
        var preview = image.Preview;
        image.Dispose();
        Assert.Null(typeof(CapturedImage).GetField("_preview", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(image));
        Assert.Throws<ObjectDisposedException>(() => image.Preview);
        preview.CopyPixels(new byte[24 * 16 * 4], 24 * 4, 0);
    }

    [Fact]
    public void ResetDetachesMosaicVisualsAndBackgroundImage()
    {
        WpfTestHost.Invoke(() =>
        {
            using var image = new CapturedImage(new Bitmap(120, 80));
            var canvas = new ImageEditorCanvas();
            canvas.Initialize(image, 120, 80);
            canvas.AddMosaicRegions([new Rect(10, 10, 40, 20)]);
            Assert.NotEmpty(canvas.Children);
            canvas.Reset();
            Assert.False(canvas.HasImage);
            Assert.Empty(canvas.Children);
            Assert.False(canvas.CanUndo);
            var cache = (System.Collections.IDictionary)typeof(ImageEditorCanvas)
                .GetField("_annotationVisuals", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(canvas)!;
            Assert.Empty(cache);
        });
    }

    [Fact]
    public void ClosingCapturePreviewDetachesItsImageAndDataContext()
    {
        WpfTestHost.Invoke(() =>
        {
            using var image = new CapturedImage(new Bitmap(96, 64));
            var window = new CapturePreviewWindow(
                image,
                Path.GetTempPath(),
                historyItem: null)
            {
                ShowActivated = false,
                Opacity = 0,
            };
            window.Show();
            window.Close();

            Assert.Null(((System.Windows.Controls.Image)window.FindName("PreviewImage")).Source);
            Assert.Null(window.DataContext);
            Assert.Throws<ObjectDisposedException>(() => _ = image.Preview);
        });
    }

    [Fact]
    public void ClosingPinnedImageDetachesRecognitionAndImageVisuals()
    {
        WpfTestHost.Invoke(() =>
        {
            var image = new CapturedImage(new Bitmap(96, 64));
            var window = new PinnedImageWindow(image)
            {
                ShowActivated = false,
                Opacity = 0,
            };
            window.Show();
            window.Close();

            Assert.Null(((System.Windows.Controls.Image)window.FindName("PinnedImage")).Source);
            Assert.Null(window.DataContext);
            Assert.Empty(((System.Windows.Controls.Canvas)window.FindName("TextOverlay")).Children);
            Assert.Throws<ObjectDisposedException>(() => _ = image.Preview);
        });
    }

    [Fact]
    public void ClosingScrollProgressDropsQueuedFullSizePreview()
    {
        WpfTestHost.Invoke(() =>
        {
            var window = new ScrollCaptureProgressWindow
            {
                ShowActivated = false,
                Opacity = 0,
            };
            using var image = new CapturedImage(new Bitmap(240, 600));
            window.QueuePreview(new ScrollCapturePreviewState(
                image.Preview,
                FrameCount: 2,
                AddedAboveFrameCount: 0,
                AddedBelowFrameCount: 1,
                PixelWidth: 240,
                PixelHeight: 600));
            window.Show();
            window.CloseFromCoordinator();

            Assert.Null(((System.Windows.Controls.Image)window.FindName("PreviewImage")).Source);
            Assert.Null(typeof(ScrollCaptureProgressWindow)
                .GetField("_pendingPreviewState", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(window));
        });
    }

    [Fact]
    public void ClosingPinnedGroupDropsCompositeAfterCloseHandlersRun()
    {
        WpfTestHost.Invoke(() =>
        {
            var first = new PinnedImageWindow(new CapturedImage(new Bitmap(80, 50)))
            {
                ShowActivated = false,
                Opacity = 0,
            };
            var second = new PinnedImageWindow(new CapturedImage(new Bitmap(60, 40)))
            {
                ShowActivated = false,
                Opacity = 0,
            };
            var group = new PinnedImageGroupWindow([first, second])
            {
                ShowActivated = false,
                Opacity = 0,
            };
            try
            {
                group.Show();
                Assert.NotNull(group.CompositePreview);
                group.Close();
                Assert.Null(typeof(PinnedImageGroupWindow)
                    .GetField("_compositePreview", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .GetValue(group));
                Assert.Null(((System.Windows.Controls.Image)group.FindName("CompositeImage")).Source);
            }
            finally
            {
                group.Close();
                first.Close();
                second.Close();
            }
        });
    }

    [Fact]
    public void ClosingTextTranslationClearsLargeTextAndLanguageSources()
    {
        WpfTestHost.Invoke(() =>
        {
            var window = new TextTranslationWindow(() => AppSettings.CreateDefault());
            window.SetSourceText(new string('x', 32_000), translateImmediately: false);
            window.Close();

            Assert.Empty(((System.Windows.Controls.TextBox)window.FindName("SourceTextBox")).Text);
            Assert.Empty(((System.Windows.Controls.TextBox)window.FindName("ResultTextBox")).Text);
            Assert.Null(((System.Windows.Controls.ComboBox)window.FindName("SourceLanguageComboBox")).ItemsSource);
            Assert.Null(((System.Windows.Controls.ComboBox)window.FindName("TargetLanguageComboBox")).ItemsSource);
        });
    }

    [Fact]
    public void ClosingOcrResultCancelsTranslationAndClearsResultText()
    {
        WpfTestHost.Invoke(() =>
        {
            using var client = new HttpClient();
            var window = new OcrResultWindow(
                new OcrRecognitionResult(true, new string('x', 32_000), null),
                AppSettings.CreateDefault(),
                new CredentialStub(),
                client)
            {
                ShowActivated = false,
                Opacity = 0,
            };
            window.Close();

            Assert.Empty(((System.Windows.Controls.TextBox)window.FindName("ResultTextBox")).Text);
            Assert.Empty(((System.Windows.Controls.TextBox)window.FindName("TranslationTextBox")).Text);
        });
    }

    [Fact]
    public void OcrIdleUnloadKeepsActiveAndRecentlyUsedEnginesButRetiresIdleSessions()
    {
        var service = typeof(HighQualityOcrService);
        const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
        var engineField = service.GetField("_engine", flags)!;
        var lastUse = service.GetField("_lastEngineUseTick", flags)!;
        var begin = service.GetMethod("BeginEngineUse", flags)!;
        var end = service.GetMethod("EndEngineUse", flags)!;
        var unload = service.GetMethod("TryUnloadIdleEngine", flags)!;
        // No model files or native sessions are needed to exercise the guards.
        // RapidOcr.Dispose throws before InitModels; ResetEngine handles that
        // same partial-initialization case in the production lifetime path.
        var engine = new RapidOcrNet.RapidOcr();
        Assert.Null(engineField.GetValue(null));
        engineField.SetValue(null, engine);
        try
        {
            begin.Invoke(null, null);
            try
            {
                lastUse.SetValue(null, Environment.TickCount64 - 60_000);
                unload.Invoke(null, null);
                Assert.Same(engine, engineField.GetValue(null));
            }
            finally { end.Invoke(null, null); }
            // Simulate a stale timer callback from before the latest request.
            unload.Invoke(null, null);
            Assert.Same(engine, engineField.GetValue(null));
            lastUse.SetValue(null, Environment.TickCount64 - 60_000);
            unload.Invoke(null, null);
            Assert.Null(engineField.GetValue(null));
        }
        finally
        {
            ((System.Threading.Timer)service.GetField("EngineUnloadTimer", flags)!.GetValue(null)!)
                .Change(Timeout.Infinite, Timeout.Infinite);
            service.GetMethod("ResetEngine", flags)!.Invoke(null, null);
        }
    }

    [Fact]
    public void HiddenSettingsAreReleasedAfterDownloadEndsAndPendingPreferencesAreSaved()
    {
        var directory = Path.Combine(Path.GetTempPath(), "SnapCut-memory-test-" + Guid.NewGuid().ToString("N"));
        var store = new SettingsStore(Path.Combine(directory, "settings.json"));
        try
        {
            WpfTestHost.Invoke(() =>
            {
                using var keys = new GlobalHotKeyManager(deferLowLevelInputHooks: true);
                var initial = AppSettings.CreateDefault() with { LaunchAtStartup = false };
                var window = new MainWindow(initial, store, new StartupStub(), keys, new CredentialStub());
                try
                {
                    Assert.Null(((System.Windows.Controls.Image)window.FindName("DonateQrImage")).Source);
                    Assert.Null(((System.Windows.Controls.Image)window.FindName("CommunityQrImage")).Source);
                    window.SaveAnnotationToolSettings([new("Arrow", "#FF123456", 7)]);
                    var idle = typeof(MainWindow).GetMethod("OnIdleMemoryTrimTimerTick", BindingFlags.Instance | BindingFlags.NonPublic)!;
                    var released = false;
                    window.HiddenIdle += (_, _) => { released = true; window.RequestExit(); };
                    var progress = (ProgressBar)window.FindName("LocalLargeModelDownloadProgressBar");
                    progress.Visibility = Visibility.Visible;
                    idle.Invoke(window, [null, EventArgs.Empty]);
                    Assert.False(released);
                    progress.Visibility = Visibility.Collapsed;
                    idle.Invoke(window, [null, EventArgs.Empty]);
                    Assert.True(released);
                    Assert.True((bool)typeof(MainWindow).GetField("_disposed", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!);
                    Assert.Equal(7, Assert.Single(store.Load().Settings.AnnotationToolSettings).StrokeWidth);
                }
                finally { window.RequestExit(); }
            });
        }
        finally
        {
            store.FlushPendingSave();
            if (File.Exists(store.SettingsPath)) File.Delete(store.SettingsPath);
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: false);
        }
    }

    private sealed class StartupStub : IStartupRegistrationService
    {
        public bool IsEnabled() => false;
        public void SetEnabled(bool enabled) { }
    }
    private sealed class CredentialStub : ITranslationCredentialStore
    {
        public string? GetApiKey(string providerId) => null;
        public void SetApiKey(string providerId, string? apiKey) { }
    }
}
