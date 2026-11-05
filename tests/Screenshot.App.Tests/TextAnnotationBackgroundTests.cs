using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Screenshot.App.Capture;
using Screenshot.App.Core;
using Screenshot.App.Editor;
using Screenshot.App.Infrastructure;
using Screenshot.App.Pin;
using Screenshot.App.Presentation;
using TextBox = System.Windows.Controls.TextBox;
using MenuItem = System.Windows.Controls.MenuItem;
using Point = System.Windows.Point;
using Color = System.Windows.Media.Color;

namespace Screenshot.App.Tests;

public sealed class TextAnnotationBackgroundTests
{
    [Fact]
    public void CaptureMenuChoiceSurvivesToolSelectionTypingAndCommit()
    {
        WpfTestHost.Invoke(() =>
        {
            var preferences = AnnotationToolPreferences.Snapshot();
            using var image = CreateImage();
            using var capture = new CaptureOverlayWindow(null, isStartupPrewarm: true, deferInitialScreenCapture: true);
            var canvas = (ImageEditorCanvas)capture.FindName("InlineEditorCanvas");
            try
            {
                AnnotationToolPreferences.Configure([], null);
                AppThemeManager.ApplySettingsPalette(capture.Resources, AppTheme.GlassInk);
                canvas.Initialize(image, 600, 300);
                var button = (System.Windows.Controls.Primitives.ToggleButton)capture.FindName("InlineTextToolButton");
                button.ContextMenu.Items.Cast<MenuItem>().Single(item => item.Tag is true)
                    .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                Assert.True(button.IsChecked);
                Assert.True(AnnotationToolPreferences.TextBackground);
                Call(canvas, "StartTextInput", new Point(20, 20));
                var input = Assert.Single(canvas.Children.OfType<TextBox>());
                input.Text = "测试ABC";
                Assert.True(Assert.IsType<SolidColorBrush>(input.Background).Color.A >= 128);
                canvas.CommitPendingText();
                var annotation = Assert.Single(Document(canvas).Annotations.OfType<TextAnnotation>());
                Assert.NotNull(annotation.Background);
                Assert.Contains(canvas.Children.OfType<Border>(), border => border.Child is TextBlock { Text: "测试ABC" });
            }
            finally { canvas.Reset(); capture.Close(); AnnotationToolPreferences.Configure(preferences, null); }
        });
    }

    [Theory]
    [InlineData(AppTheme.NeonDeep)]
    [InlineData(AppTheme.GlassInk)]
    [InlineData(AppTheme.GlassMist)]
    [InlineData(AppTheme.AuroraMist)]
    public void TypedBackgroundFitsTextAndFontAndSurvivesCommitExportAndThemeChange(AppTheme theme)
    {
        WpfTestHost.Invoke(() =>
        {
            var preferences = AnnotationToolPreferences.Snapshot();
            using var image = CreateImage();
            var canvas = CreateCanvas(image, theme);
            try
            {
                AnnotationToolPreferences.Configure([], null);
                canvas.SelectTool(EditorTool.Text);
                canvas.SetStrokeWidth(6);
                canvas.SetTextBackground(true);
                Call(canvas, "StartTextInput", new Point(30, 40));
                var input = Assert.Single(canvas.Children.OfType<TextBox>());
                input.Text = "测试ABC";
                var shortWidth = input.Width;
                input.Text += " · 更长的文字";
                Assert.True(input.Width > shortWidth);
                input.Text = "测试ABC";
                Assert.Equal(shortWidth, input.Width);
                Assert.True(input.Width < 420);
                canvas.SetStrokeWidth(8);
                Assert.True(input.Width > shortWidth);
                var editingWidth = input.Width;
                var inputFill = Assert.IsType<SolidColorBrush>(input.Background).Color;
                canvas.CommitPendingText();

                var text = Assert.Single(Document(canvas).Annotations.OfType<TextAnnotation>());
                Assert.Equal(inputFill, text.Background!.Fill);
                Assert.Equal(40, text.FontSize);
                var bounds = canvas.GetAnnotationBounds()!.Value;
                Assert.InRange(Math.Abs(bounds.Width - editingWidth), 0, 2);
                var rendered = canvas.RenderEditedImage();
                var pixel = ReadPixel(rendered, 32, (int)(40 + bounds.Height / 2));
                // The input image is opaque black; compare the composited fill, including glass alpha.
                Assert.InRange(Math.Abs(pixel.R - inputFill.R * inputFill.A / 255d), 0, 2);
                Assert.InRange(Math.Abs(pixel.G - inputFill.G * inputFill.A / 255d), 0, 2);
                Assert.InRange(Math.Abs(pixel.B - inputFill.B * inputFill.A / 255d), 0, 2);
                AppThemeManager.ApplySettingsPalette(canvas.Resources, AppTheme.CoralSky);
                Assert.Equal(pixel, ReadPixel(canvas.RenderEditedImage(), 32, (int)(40 + bounds.Height / 2)));

                if (Environment.GetEnvironmentVariable("SNAPCUT_TEXT_BACKGROUND_RENDER") is { Length: > 0 } directory)
                {
                    Directory.CreateDirectory(directory);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(rendered));
                    using var file = File.Create(Path.Combine(directory, $"text-{theme}.png")); encoder.Save(file);
                }
            }
            finally { canvas.Reset(); AnnotationToolPreferences.Configure(preferences, null); }
        });
    }

    [Fact]
    public void ReeditingResizesThePlateAndRemovingBackgroundSupportsUndoRedo()
    {
        WpfTestHost.Invoke(() =>
        {
            var preferences = AnnotationToolPreferences.Snapshot();
            using var image = CreateImage();
            var canvas = CreateCanvas(image, AppTheme.NeonDeep);
            try
            {
                AnnotationToolPreferences.Configure([], null);
                canvas.SelectTool(EditorTool.Text);
                canvas.SetTextBackground(true);
                Call(canvas, "StartTextInput", new Point(20, 20));
                Assert.Single(canvas.Children.OfType<TextBox>()).Text = "输入完成后保留背景";
                canvas.PrepareTextBackgroundOptions();
                var original = Assert.Single(Document(canvas).Annotations.OfType<TextAnnotation>());
                var originalWidth = canvas.GetAnnotationBounds()!.Value.Width;
                Call(canvas, "StartExistingTextInput", original, 0);
                Assert.Single(canvas.Children.OfType<TextBox>()).Text = "ABC";
                canvas.CommitPendingText();
                Assert.True(canvas.GetAnnotationBounds()!.Value.Width < originalWidth);
                Assert.Equal(original.Background, Assert.Single(Document(canvas).Annotations.OfType<TextAnnotation>()).Background);
                canvas.SetTextBackground(false);
                Assert.Null(Assert.Single(Document(canvas).Annotations.OfType<TextAnnotation>()).Background);
                canvas.Undo();
                Assert.NotNull(Assert.Single(Document(canvas).Annotations.OfType<TextAnnotation>()).Background);
                canvas.Redo();
                Assert.Null(Assert.Single(Document(canvas).Annotations.OfType<TextAnnotation>()).Background);
                Assert.Equal(Colors.Black, ReadPixel(canvas.RenderEditedImage(), 19, 19));
            }
            finally { canvas.Reset(); AnnotationToolPreferences.Configure(preferences, null); }
        });
    }

    [Fact]
    public void BackgroundPreferencePersistsWithTextStyleAndIsSharedByNewCanvases()
    {
        var directory = Path.Combine(Path.GetTempPath(), "SnapCut-text-background-" + Guid.NewGuid().ToString("N"));
        var store = new SettingsStore(Path.Combine(directory, "settings.json"));
        WpfTestHost.Invoke(() =>
        {
            var preferences = AnnotationToolPreferences.Snapshot();
            try
            {
                AnnotationToolPreferences.Configure([new("Text", "#FFFF0000", 8)],
                    settings => store.QueueSave(AppSettings.CreateDefault() with { AnnotationToolSettings = settings }));
                AnnotationToolPreferences.SetTextBackground(true);
                store.FlushPendingSave();
                AnnotationToolPreferences.Configure(new SettingsStore(store.SettingsPath).Load().Settings.AnnotationToolSettings, null);
                Assert.True(AnnotationToolPreferences.TextBackground);
                Assert.Equal(8, AnnotationToolPreferences.GetWidth(EditorTool.Text, 3));
                Assert.Equal(Colors.Red, AnnotationToolPreferences.GetColor(EditorTool.Text, Colors.Black));
                using var image = CreateImage();
                var canvas = CreateCanvas(image, AppTheme.NeonDeep);
                canvas.SelectTool(EditorTool.Text);
                Call(canvas, "StartTextInput", new Point(20, 20));
                Assert.True(canvas.TextBackgroundEnabled);
                Assert.Equal(40, Assert.Single(canvas.Children.OfType<TextBox>()).FontSize);
                canvas.Reset();

                AnnotationToolPreferences.Configure(AnnotationToolPreferences.Snapshot(),
                    settings => store.QueueSave(AppSettings.CreateDefault() with { AnnotationToolSettings = settings }));
                AnnotationToolPreferences.SetTextBackground(false);
                store.FlushPendingSave();
                AnnotationToolPreferences.Configure(store.Load().Settings.AnnotationToolSettings, null);
                Assert.False(AnnotationToolPreferences.TextBackground);
            }
            finally
            {
                AnnotationToolPreferences.Configure(preferences, null);
                store.FlushPendingSave();
                if (File.Exists(store.SettingsPath)) File.Delete(store.SettingsPath);
                if (Directory.Exists(directory)) Directory.Delete(directory, false);
            }
        });
    }

    [Fact]
    public void ScreenshotPinAndLongImageMenusShareTheChoiceAndRecordingCanvasUsesIt()
    {
        WpfTestHost.Invoke(() =>
        {
            var preferences = AnnotationToolPreferences.Snapshot();
            using var ownerImage = CreateImage();
            PinnedImageWindow? pin = null;
            PinnedImageEditorToolbarWindow? pinnedToolbar = null;
            CaptureOverlayWindow? capture = null;
            ImageEditorWindow? longEditor = null;
            RecordingAnnotationOverlayWindow? recording = null;
            try
            {
                pin = new PinnedImageWindow(ownerImage);
                _ = new System.Windows.Interop.WindowInteropHelper(pin).EnsureHandle();
                pinnedToolbar = new PinnedImageEditorToolbarWindow(pin);
                capture = new CaptureOverlayWindow(null, isStartupPrewarm: true, deferInitialScreenCapture: true);
                longEditor = new ImageEditorWindow(CreateImage(), Path.GetTempPath());
                recording = new RecordingAnnotationOverlayWindow(new ScreenRegion(0, 0, 600, 300));
                AnnotationToolPreferences.Configure([], null);
                foreach (var (window, name) in new[] { ((Window)capture, "InlineTextToolButton"),
                    ((Window)pinnedToolbar, "TextToolButton"), ((Window)longEditor, "TextToolButton") })
                {
                    var button = (System.Windows.Controls.Primitives.ToggleButton)window.FindName(name);
                    Assert.Equal(2, button.ContextMenu.Items.Count);
                    var on = button.ContextMenu.Items.Cast<MenuItem>().Single(item => item.Tag is true);
                    on.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                    Assert.True(AnnotationToolPreferences.TextBackground);
                    var off = button.ContextMenu.Items.Cast<MenuItem>().Single(item => item.Tag is false);
                    off.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                    Assert.False(AnnotationToolPreferences.TextBackground);
                }
                AnnotationToolPreferences.SetTextBackground(true);
                Call(recording.AnnotationCanvas, "StartTextInput", new Point(20, 20));
                Assert.Single(recording.AnnotationCanvas.Children.OfType<TextBox>()).Text = "录屏文字";
                recording.AnnotationCanvas.CommitPendingText();
                Assert.NotNull(Assert.Single(Document(recording.AnnotationCanvas).Annotations.OfType<TextAnnotation>()).Background);
            }
            finally
            {
                recording?.Close(); longEditor?.Close(); pinnedToolbar?.Close(); pin?.Close();
                capture?.Dispose();
                AnnotationToolPreferences.Configure(preferences, null);
            }
        });
    }

    private static CapturedImage CreateImage()
    {
        var bitmap = new System.Drawing.Bitmap(600, 300);
        using (var graphics = System.Drawing.Graphics.FromImage(bitmap)) graphics.Clear(System.Drawing.Color.Black);
        return new CapturedImage(bitmap);
    }
    private static ImageEditorCanvas CreateCanvas(CapturedImage image, AppTheme theme)
    {
        var canvas = new ImageEditorCanvas();
        canvas.Initialize(image, image.PixelWidth, image.PixelHeight);
        AppThemeManager.ApplySettingsPalette(canvas.Resources, theme);
        return canvas;
    }
    private static EditorDocument Document(ImageEditorCanvas canvas) => (EditorDocument)typeof(ImageEditorCanvas)
        .GetField("_document", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(canvas)!;
    private static void Call(object target, string name, params object[] arguments) => target.GetType()
        .GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(target, arguments);
    private static Color ReadPixel(BitmapSource image, int x, int y)
    {
        var pixel = new byte[4]; image.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
        return Color.FromArgb(pixel[3], pixel[2], pixel[1], pixel[0]);
    }
}
