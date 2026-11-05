using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Screenshot.App.Capture;
using Screenshot.App.Core;
using Screenshot.App.Editor;
using Screenshot.App.Infrastructure;
using DrawingRectangle = System.Drawing.Rectangle;

namespace Screenshot.App.Tests;

public sealed class RecordingToolbarDockTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DoubleArrowMenuSwitchesRecordingToolsAndRestoresTheSavedHollowVariantOffScreen(bool prepare)
    {
        WpfTestHost.Invoke(() =>
        {
            var saved = AnnotationToolPreferences.Snapshot();
            VideoRecordingControlWindow? window = null;
            try
            {
                AnnotationToolPreferences.Configure([], null);
                AnnotationToolPreferences.SetVariant(EditorTool.DoubleArrow, EditorTool.HollowDoubleArrow);
                AppThemeManager.ApplySettingsPalette(Application.Current.Resources, AppTheme.GlassMist);
                window = CreateWindow(Path.GetTempPath(), prepare);
                AppThemeManager.ApplySettingsPalette(window.Resources, AppTheme.GlassMist);
                var button = (System.Windows.Controls.Primitives.ToggleButton)window.FindName("DoubleArrowToolButton");
                Assert.Equal("HollowDoubleArrow", button.Tag);
                Assert.Equal(2, button.ContextMenu.Items.Count);
                for (var index = 0; index < 2; index++)
                {
                    ((MenuItem)button.ContextMenu.Items[index]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                    Assert.True(button.IsChecked);
                    var overlay = (RecordingAnnotationOverlayWindow)typeof(VideoRecordingControlWindow)
                        .GetField("_annotationOverlay", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                    Assert.Equal(index == 0 ? EditorTool.DoubleArrow : EditorTool.HollowDoubleArrow, (EditorTool)typeof(ImageEditorCanvas).GetField("_selectedTool", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(overlay.AnnotationCanvas)!);
                    Assert.False(window.IsVisible);
                    Assert.False(overlay.IsVisible);
                }
                var panel = (FrameworkElement)window.FindName("AnnotationToolsPanel");
                // Render the real recording toolbar without showing a desktop window.
                panel.Visibility = Visibility.Visible;
                panel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                panel.Arrange(new Rect(new Point(), panel.DesiredSize));
                panel.UpdateLayout();
                Assert.True(button.ActualWidth >= 36);
                if (Environment.GetEnvironmentVariable("SNAPCUT_ANNOTATION_PREVIEW_DIR") is { Length: > 0 } directory)
                {
                    Directory.CreateDirectory(directory);
                    var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(panel.ActualWidth),
                        (int)Math.Ceiling(panel.ActualHeight), 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                    bitmap.Render(panel);
                    var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                    using var stream = File.Create(Path.Combine(directory, "recording-GlassMist.png"));
                    encoder.Save(stream);
                }
            }
            finally { window?.Close(); AnnotationToolPreferences.Configure(saved, null); }
        });
    }

    [Theory]
    [InlineData(-150, -30)]
    [InlineData(1900, 1060)]
    [InlineData(-2000, 1200)]
    public void DockClampingKeepsEntireButtonInsideEachMonitor(int x, int y)
    {
        foreach (var screen in new[] { new DrawingRectangle(0, 0, 1920, 1080), new DrawingRectangle(-1920, -200, 1920, 1080) })
        {
            var bounds = VideoRecordingControlWindow.ConstrainDockBounds(new DrawingRectangle(x, y, 180, 66), screen);
            Assert.True(screen.Contains(bounds));
            Assert.Equal(180, bounds.Width);
            Assert.Equal(66, bounds.Height);
        }
    }

    [DesktopTheory]
    [InlineData(AppTheme.GlassMist)]
    [InlineData(AppTheme.GlassInk)]
    [InlineData(AppTheme.ForestNight)]
    public async Task DockKeepsStatusReadableAndSurvivesInterruptedTransitions(AppTheme theme)
    {
        VideoRecordingControlWindow? window = null;
        var directory = Path.Combine(Path.GetTempPath(), "SnapCut-Dock-Tests", Guid.NewGuid().ToString("N"));
        try
        {
            WpfTestHost.Invoke(() =>
            {
                window = CreateWindow(directory);
                AppThemeManager.ApplySettingsPalette(window.Resources, theme);
                window.Show();
                window.UpdateLayout();
                ((TextBlock)window.FindName("RecordingStatusText")).Text = "正在录屏";
                ((TextBlock)window.FindName("ElapsedTimeText")).Text = "01:23:45";
                Call(window, "CollapseToolbarToEdge");
            });
            await Task.Delay(380);
            WpfTestHost.Invoke(() =>
            {
                AssertCollapsed(window!);
                var time = (TextBlock)window!.FindName("CollapsedElapsedText");
                Assert.Equal("01:23:45", time.Text);
                var reference = new TextBlock { Text = time.Text, FontFamily = time.FontFamily, FontSize = time.FontSize };
                reference.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                Assert.True(time.ActualWidth >= reference.DesiredSize.Width);
                var pause = (System.Windows.Shapes.Path)window.FindName("CollapsedPauseIcon");
                Assert.Equal(Visibility.Collapsed, pause.Visibility);
                ((TextBlock)window.FindName("RecordingStatusText")).Text = "已暂停";
                window.UpdateLayout();
                Assert.Equal(Visibility.Visible, pause.Visibility);
                // Reverse expansion while its travel is still active.
                Call(window, "ExpandToolbarFromEdge");
                Call(window, "CollapseToolbarToEdge");
            });
            await Task.Delay(380);
            WpfTestHost.Invoke(() =>
            {
                AssertCollapsed(window!);
                Call(window!, "ExpandToolbarFromEdge");
            });
            await Task.Delay(380);
            WpfTestHost.Invoke(() =>
            {
                Assert.True(((FrameworkElement)window!.FindName("ControlSurface")).IsVisible);
                Assert.False(((FrameworkElement)window.FindName("CollapsedToolbarButton")).IsVisible);
                Assert.True(window.ActualWidth > 300);
                // Stopping during collapse must show the completion surface;
                // a stale continuation must never bring the dock back later.
                Call(window, "CollapseToolbarToEdge");
                Call(window, "EnterCompletionUi");
            });
            await Task.Delay(380);
            WpfTestHost.Invoke(() =>
            {
                Assert.True(((FrameworkElement)window!.FindName("ControlSurface")).IsVisible);
                Assert.False(((FrameworkElement)window.FindName("CollapsedToolbarButton")).IsVisible);
                Assert.Equal(1, ((FrameworkElement)window.FindName("ControlSurface")).Opacity);
                Call(window, "CollapseToolbarToEdge");
                window.Close();
            });
            await Task.Delay(300);
        }
        finally
        {
            WpfTestHost.Invoke(() => window?.Close());
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private static void AssertCollapsed(VideoRecordingControlWindow window)
    {
        var button = (Button)window.FindName("CollapsedToolbarButton");
        Assert.True(button.IsVisible);
        Assert.False(((FrameworkElement)window.FindName("ControlSurface")).IsVisible);
        var origin = button.TranslatePoint(new Point(), window);
        Assert.True(origin.X >= 0 && origin.Y >= 0);
        Assert.True(origin.X + button.ActualWidth <= window.ActualWidth + 0.5);
        Assert.True(origin.Y + button.ActualHeight <= window.ActualHeight + 0.5);
        Assert.True(MonitorGeometryService.TryGetWindowBounds(window, out var bounds));
        Assert.True(System.Windows.Forms.Screen.FromRectangle(bounds).WorkingArea.Contains(bounds));
    }

    private static VideoRecordingControlWindow CreateWindow(string directory, bool prepare = false)
    {
        var screen = System.Windows.Forms.Screen.PrimaryScreen!.WorkingArea;
        var region = new ScreenRegion(screen.X + 30, screen.Y + 30, 320, 240);
        var recorder = new RegionVideoRecorder(region, directory);
        var input = new RecordingInputOverlayWindow(region);
        var annotation = new RecordingAnnotationOverlayWindow(region);
        try
        {
            var constructor = typeof(VideoRecordingControlWindow).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single();
            var window = prepare ? VideoRecordingControlWindow.PrepareForResidentSelection()
                : (VideoRecordingControlWindow)constructor.Invoke([]);
            var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            typeof(VideoRecordingControlWindow).GetMethod("InitializeSession", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [
                recorder, directory, false, false, VideoRecordingCodec.H264, 30,
                false, false, false, false, null, null, VideoRecordingOutputFormat.Mp4,
                input, annotation, null,
                new VideoRecordingAnnotationPreferences(ShapeToolMode.Rectangle, ArrowToolMode.Straight, ArrowStyle.Filled, "#F04455", 3),
                null, null, null, "Alt+2", 85d, null, null,
            ]);
            if (prepare) Assert.Equal(handle, new System.Windows.Interop.WindowInteropHelper(window).Handle);
            Assert.Equal(.85, ((System.Windows.Media.ScaleTransform)window.FindName("ToolbarLayoutScale")).ScaleX);
            return window;
        }
        catch { recorder.Dispose(); input.Close(); annotation.Close(); throw; }
    }

    private static void Call(VideoRecordingControlWindow window, string method) =>
        typeof(VideoRecordingControlWindow).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
}
