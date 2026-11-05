using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Screenshot.App.Capture;
using Screenshot.App.Core;
using Screenshot.App.Infrastructure;
using Screenshot.App.Pin;
using Screenshot.App.Text;
using Xunit.Abstractions;

namespace Screenshot.App.Tests;

public sealed class PreparedCaptureToolbarTests(ITestOutputHelper output)
{
    [Fact]
    public void PreparedFrameIsReusedOnlyByMatchingSelectionAndDisposedOnReplacementOrCancel()
    {
        WpfTestHost.Invoke(() =>
        {
            using var pins = new PinnedImageManager();
            using var http = new HttpClient();
            using var coordinator = new RegionCaptureCoordinator(AppSettings.CreateDefault, new CaptureHistoryService(), pins,
                new DpapiTranslationCredentialStore(), http, _ => { });
            CapturedImage Frame() => new(new System.Drawing.Bitmap(5, 5));
            using var first = Frame();
            using var second = Frame();
            coordinator.PrepareResidentFrame(1, first);
            coordinator.PrepareResidentFrame(2, second);
            Assert.Throws<ObjectDisposedException>(() => first.Preview);
            Assert.Null(coordinator.TakePreparedResidentFrame(1));
            Assert.Throws<ObjectDisposedException>(() => second.Preview);
            using var third = Frame();
            coordinator.PrepareResidentFrame(3, third);
            Assert.Same(third, coordinator.TakePreparedResidentFrame(3));
            Assert.Null(coordinator.TakePreparedResidentFrame(3));
            coordinator.DiscardPreparedCaptureToolbar();
            Assert.NotNull(third.Preview);
            using var last = Frame();
            coordinator.PrepareResidentFrame(4, last);
            coordinator.DiscardPreparedCaptureToolbar();
            Assert.Throws<ObjectDisposedException>(() => last.Preview);
        });
    }

    [Theory]
    [InlineData(AppTheme.AuroraMist, 83, CaptureToolbarRowCount.One)]
    [InlineData(AppTheme.GlassInk, 150, CaptureToolbarRowCount.Two)]
    public void PreparedToolbarIsTheActualSessionAndKeepsItsConfiguration(
        AppTheme theme, double scale, CaptureToolbarRowCount rows)
    {
        WpfTestHost.Invoke(() =>
        {
            AppThemeManager.ApplySettingsPalette(Application.Current.Resources, theme);
            using var pins = new PinnedImageManager();
            using var http = new HttpClient();
            var settings = AppSettings.CreateDefault() with { Theme = theme, ToolbarScalePercent = scale,
                CaptureToolbarRows = rows, VisibleCaptureToolbarFeatures = [CaptureToolbarFeature.Text, CaptureToolbarFeature.Translation] };
            var persisted = 0;
            var coordinator = new RegionCaptureCoordinator(() => settings, new CaptureHistoryService(), pins,
                new DpapiTranslationCredentialStore(), http, _ => { }, lastAnnotationToolChanged: _ => persisted++);
            var clock = Stopwatch.StartNew();
            coordinator.PrepareResidentCaptureToolbar();
            output.WriteLine($"Prepare {theme}: {clock.Elapsed.TotalMilliseconds:F1} ms");
            var prepared = (CaptureOverlayWindow)typeof(RegionCaptureCoordinator)
                .GetField("_preparedCaptureOverlay", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(coordinator)!;
            var toolbar = (Border)prepared.FindName("CaptureToolbar");
            Assert.True(((FrameworkElement)prepared.FindName("TranslateButton")).ActualWidth > 0);
            Assert.False(prepared.IsVisible);
            Assert.Equal(0, toolbar.Opacity);
            var preparedHandle = new WindowInteropHelper(prepared).Handle;
            Assert.NotEqual(IntPtr.Zero, preparedHandle);
            Assert.False(IsWindowVisible(preparedHandle));
            Assert.True(prepared.CanCompletePreparedSession);
            Assert.Null(((Image)prepared.FindName("FrozenScreenImage")).Source);
            Assert.Equal(scale / 100, ((ScaleTransform)toolbar.LayoutTransform).ScaleX);
            Assert.Equal(Visibility.Collapsed, ((FrameworkElement)prepared.FindName("SaveButton")).Visibility);
            Assert.Equal(Visibility.Visible, ((FrameworkElement)prepared.FindName("TranslateButton")).Visibility);
            coordinator.PrepareResidentCaptureToolbar();
            Assert.Same(prepared, coordinator.TakePreparedCaptureToolbar(settings, true));
            Assert.Null(coordinator.TakePreparedCaptureToolbar(settings, true));
            var options = new CaptureOverlayOptions { SaveDirectory = "", KeepHistory = false, HistoryLimit = 0,
                HistoryService = new CaptureHistoryService(), PinnedImageManager = pins,
                StartOcrAsync = _ => Task.CompletedTask, InitialSelection = new ScreenRegion(0, 0, 16, 16),
                CaptureClosed = () => persisted++ };
            using var image = new CapturedImage(new System.Drawing.Bitmap(16, 16));
            _ = image.WarmPreview();
            clock.Restart();
            prepared.CompletePreparedSession(options, image);
            output.WriteLine($"Adopt {theme}: {clock.Elapsed.TotalMilliseconds:F1} ms");
            Assert.Same(toolbar, prepared.FindName("CaptureToolbar"));
            Assert.Equal(preparedHandle, new WindowInteropHelper(prepared).Handle);
            Assert.Same(image.Preview, ((Image)prepared.FindName("FrozenScreenImage")).Source);
            Assert.Equal(1, prepared.Opacity);
            Assert.False(prepared.CanCompletePreparedSession);
            Assert.False(prepared.IsVisible);
            Assert.Equal(0, persisted);
            prepared.Close();
            Assert.Equal(1, persisted);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CancelOrChangedSettingsDiscardsPreparationWithoutSavingPreferences(bool changeSettings)
    {
        WpfTestHost.Invoke(() =>
        {
            using var pins = new PinnedImageManager();
            using var http = new HttpClient();
            var settings = AppSettings.CreateDefault();
            var saved = 0;
            var coordinator = new RegionCaptureCoordinator(() => settings, new CaptureHistoryService(), pins,
                new DpapiTranslationCredentialStore(), http, _ => { }, lastAnnotationToolChanged: _ => saved++);
            coordinator.PrepareResidentCaptureToolbar();
            var prepared = (CaptureOverlayWindow)typeof(RegionCaptureCoordinator)
                .GetField("_preparedCaptureOverlay", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(coordinator)!;
            if (changeSettings) settings = settings with { ToolbarScalePercent = 125 };
            Assert.Null(coordinator.TakePreparedCaptureToolbar(settings, changeSettings));
            coordinator.DiscardPreparedCaptureToolbar();
            Assert.False(prepared.CanCompletePreparedSession);
            Assert.False(IsWindow(new WindowInteropHelper(prepared).Handle));
            Assert.Equal(0, saved);
            coordinator.PrepareResidentCaptureToolbar();
            var next = coordinator.TakePreparedCaptureToolbar(settings, true);
            Assert.NotNull(next);
            next.DiscardPreparedSession();
        });
    }

    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
}
