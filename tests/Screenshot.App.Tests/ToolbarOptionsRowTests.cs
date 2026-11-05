using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Screenshot.App.Capture;
using Screenshot.App.Core;
using Screenshot.App.Editor;
using Screenshot.App.Infrastructure;
using Screenshot.App.Presentation;
using Screenshot.App.Text;

namespace Screenshot.App.Tests;

public sealed class ToolbarOptionsRowTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Theory]
    [InlineData("button")]
    [InlineData("glyph")]
    [InlineData("run")]
    public void TranslationRightClickDoesNotUndoOrArmCaptureExit(string hit)
    {
        WpfTestHost.Invoke(() =>
        {
            using var window = new CaptureOverlayWindow(null, isStartupPrewarm: true, deferInitialScreenCapture: true);
            try
            {
                typeof(CaptureOverlayWindow).GetField("_initialScreenInputReady", Private)!.SetValue(window, true);
                var root = (FrameworkElement)window.Content;
                window.Content = null;
                root.Resources = window.Resources;
                using var source = new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters("Hidden translation input route")
                    { WindowStyle = unchecked((int)0x80000000), Width = 1200, Height = 900 });
                source.RootVisual = root;
                var surface = (Grid)window.FindName("CaptureSurface");
                var toolbar = (Border)window.FindName("CaptureToolbar");
                toolbar.Visibility = Visibility.Visible;
                Call(window, "UpdateSelectionBounds", new Rect(20, 20, 160, 120));
                using var image = new CapturedImage(new System.Drawing.Bitmap(160, 120));
                var editor = (ImageEditorCanvas)window.FindName("InlineEditorCanvas");
                editor.Initialize(image, 160, 120);
                var button = (Button)window.FindName("TranslateButton");
                var glyph = (TextBlock)window.FindName("TranslateButtonText");
                var run = new System.Windows.Documents.Run("译");
                if (hit == "run") { glyph.Inlines.Clear(); glyph.Inlines.Add(run); }
                root.Measure(new Size(1200, 900)); root.Arrange(new Rect(0, 0, 1200, 900)); root.UpdateLayout();
                var downCount = 0;
                var upCount = 0;
                surface.AddHandler(UIElement.PreviewMouseRightButtonDownEvent,
                    new MouseButtonEventHandler((_, _) => downCount++), true);
                surface.AddHandler(UIElement.PreviewMouseRightButtonUpEvent,
                    new MouseButtonEventHandler((_, _) => upCount++), true);
                void RightClickTranslation()
                {
                    foreach (var routedEvent in new[] { Mouse.PreviewMouseDownEvent, Mouse.PreviewMouseUpEvent })
                    {
                        var args = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Right) { RoutedEvent = routedEvent };
                        if (hit == "button") button.RaiseEvent(args);
                        else if (hit == "glyph") glyph.RaiseEvent(args);
                        else run.RaiseEvent(args);
                        Assert.False(args.Handled);
                    }
                    Assert.False((bool)typeof(CaptureOverlayWindow).GetField("_completeAfterRightButtonUp", Private)!.GetValue(window)!);
                }
                RightClickTranslation(); // No annotations: background right-click would arm exit.
                Assert.Equal(1, downCount);
                Assert.Equal(1, upCount);
                var selection = (System.Windows.Shapes.Rectangle)window.FindName("SelectionRectangle");
                Assert.Equal(160, selection.Width);
                Assert.Equal(120, selection.Height);
                Assert.False((bool)typeof(CaptureOverlayWindow).GetField("_completeAfterRightButtonUp", Private)!.GetValue(window)!);
                Assert.Empty(button.ContextMenu.Items);
                Assert.DoesNotContain("F4", Assert.IsType<string>(button.ToolTip));
                // Opening remains lazy; the separate menu/translation test checks language selection.
                button.ContextMenu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent));
                Assert.NotEmpty(button.ContextMenu.Items);

                var document = (EditorDocument)typeof(ImageEditorCanvas).GetField("_document", Private)!.GetValue(editor)!;
                document.Add(new RectangleAnnotation(new Rect(4, 4, 20, 16), Colors.Red, 3));
                Call(editor, "RebuildCanvas");
                RightClickTranslation(); // Existing annotations must not be undone by the menu gesture.
                Assert.True(editor.CanUndo);

                var background = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Right)
                    { RoutedEvent = Mouse.PreviewMouseDownEvent };
                surface.RaiseEvent(background);
                Assert.True(background.Handled);
                Assert.False(editor.CanUndo);
                Assert.True(editor.HasImage);
                Assert.False(window.IsVisible);
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData(Key.F1)]
    [InlineData(Key.F4)]
    [InlineData(Key.F12)]
    public void TranslationLanguageMenuLeavesFunctionKeysAvailable(Key key)
    {
        WpfTestHost.Invoke(() =>
        {
            var button = new Button();
            TranslationLanguageMenu.SetInlineOptions(button, true);
            TranslationLanguageMenu.SetEnabled(button, true);
            using var source = new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters("Hidden translation keys")
                { WindowStyle = unchecked((int)0x80000000), Width = 10, Height = 10 });
            source.RootVisual = button;
            var args = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            try
            {
                button.RaiseEvent(args);
                Assert.False(args.Handled);
                Assert.False(button.ContextMenu.IsOpen);
                Assert.Empty(button.ContextMenu.Items);
            }
            finally { button.ContextMenu.IsOpen = false; }
        });
    }

    [Theory]
    [InlineData(500, 250)]
    [InlineData(900, 250)]
    [InlineData(900, 650)]
    public void EmojiUsesThreeRowsWithSizeOnTheRightAndKeepsToolbarAnchor(double rowWidth, double top)
    {
        WpfTestHost.Invoke(() =>
        {
            using var window = new CaptureOverlayWindow(null, isStartupPrewarm: true, deferInitialScreenCapture: true);
            try
            {
                AppThemeManager.ApplySettingsPalette(window.Resources, AppTheme.GlassInk);
                var root = (FrameworkElement)window.Content;
                window.Content = null;
                root.Resources = window.Resources;
                using var source = new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters("Hidden emoji layout")
                    { WindowStyle = unchecked((int)0x80000000), Width = 1200, Height = 900 });
                source.RootVisual = root;
                var toolbar = (Border)window.FindName("CaptureToolbar");
                toolbar.Visibility = Visibility.Visible;
                var status = (TextBlock)window.FindName("CaptureStatusText");
                status.Text = "可继续编辑当前截图。";
                status.Visibility = Visibility.Visible;
                var rectangle = (System.Windows.Shapes.Rectangle)window.FindName("SelectionRectangle");
                rectangle.Width = 677; rectangle.Height = 397;
                Canvas.SetLeft(rectangle, 250); Canvas.SetTop(rectangle, 40);
                var line = (ToolbarAdjustmentPanel)window.FindName("InlineAdjustmentLine");
                var widthSource = new Border { Width = rowWidth, Height = 32 };
                widthSource.Measure(new Size(rowWidth, 32)); widthSource.Arrange(new Rect(0, 0, rowWidth, 32));
                line.WidthSource = widthSource;
                line.ReservedWidthSource = (FrameworkElement)window.FindName("InlineStatusRow");
                Canvas.SetLeft(toolbar, 20); Canvas.SetTop(toolbar, top);
                root.Measure(new Size(1200, 900)); root.Arrange(new Rect(0, 0, 1200, 900)); root.UpdateLayout();
                var originalWidth = toolbar.ActualWidth;
                ((RadioButton)window.FindName("InlineEmojiToolButton")).IsChecked = true;
                root.UpdateLayout();
                var palette = (ToolbarEmojiStrip)window.FindName("InlineEmojiPalette");
                var size = (StackPanel)window.FindName("InlineStrokeOptions");
                var position = size.TranslatePoint(new Point(0, size.ActualHeight / 2), line);
                Assert.Equal(102, line.ActualHeight);
                Assert.True(position.X >= palette.ActualWidth);
                Assert.InRange(Math.Abs(position.Y - palette.ActualHeight / 2), 0, 2);
                Assert.InRange(palette.ViewportWidth, rowWidth - 350, rowWidth - 180);
                Assert.Equal(originalWidth, toolbar.ActualWidth);
                Assert.Equal(0, palette.ScrollableWidth);
                Assert.Equal(102, palette.ViewportHeight);
                Assert.True(palette.ScrollableHeight > 0);
                var emojis = (WrapPanel)palette.Content;
                foreach (FrameworkElement emoji in emojis.Children)
                {
                    var bounds = emoji.TransformToAncestor(emojis).TransformBounds(new Rect(emoji.RenderSize));
                    Assert.InRange(bounds.Left, 0, palette.ViewportWidth);
                    Assert.True(bounds.Right <= palette.ViewportWidth);
                }
                palette.ScrollToBottom(); root.UpdateLayout();
                Assert.True(palette.VerticalOffset > 0);
                var last = (FrameworkElement)emojis.Children[^1];
                Assert.True(last.TranslatePoint(new Point(0, last.ActualHeight), palette).Y <= palette.ViewportHeight);
                Assert.Equal(20, Canvas.GetLeft(toolbar));
                Assert.Equal(top, Canvas.GetTop(toolbar));
                toolbar.Resources = window.Resources;
                palette.ScrollToTop(); root.UpdateLayout();
                RenderToolbar(toolbar, $"capture-emoji-{rowWidth}-{top}");
                Assert.False(window.IsVisible);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void TranslationTargetCanBeChosenBeforeStartingAndTheChoiceIsUsed()
    {
        WpfTestHost.Invoke(() =>
        {
            var previous = TranslationTargetPreferences.Language ?? "zh-Hans";
            TranslationTargetPreferences.Configure("zh-Hans", _ => { });
            using var window = new CaptureOverlayWindow(null, isStartupPrewarm: true, deferInitialScreenCapture: true);
            var button = (Button)window.FindName("TranslateButton");
            var requests = 0;
            button.Click += (_, _) => requests++;
            try
            {
                Assert.Empty(button.ContextMenu.Items);
                button.ContextMenu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent));
                Assert.Equal(0, requests);
                var japanese = button.ContextMenu.Items.OfType<MenuItem>().Single(item => item.Tag as string == "ja");
                japanese.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                Assert.Equal(0, requests);
                Assert.Equal("ja", TranslationTargetPreferences.Language);
                Assert.Contains("日本", Assert.IsType<string>(button.ToolTip));
                Assert.Contains("右键", Assert.IsType<string>(button.ToolTip));
                button.ContextMenu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent));
                Assert.True(japanese.IsChecked);
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(1, requests);
                Assert.Empty(((ToolbarOptionsRow)window.FindName("InlineActionAdjustments")).Children);
            }
            finally { window.Close(); TranslationTargetPreferences.Configure(previous, _ => { }); }
        });
    }

    [Theory]
    [InlineData(950, false)]
    [InlineData(330, true)]
    public void AdjustmentGroupsShareOneRowWhenTheyFitAndWrapWhenNarrow(double width, bool wrap)
    {
        WpfTestHost.Invoke(() =>
        {
            using var window = new CaptureOverlayWindow(null, isStartupPrewarm: true, deferInitialScreenCapture: true);
            try
            {
                ((RadioButton)window.FindName("InlineShapeToolButton")).IsChecked = true;
                var line = (ToolbarAdjustmentPanel)window.FindName("InlineAdjustmentLine");
                ((Panel)line.Parent).Children.Remove(line);
                line.Resources = window.Resources;
                var source = new Border { Width = width, Height = 32 };
                source.Measure(new Size(width, 32)); source.Arrange(new Rect(0, 0, width, 32));
                line.WidthSource = source;
                var host = new Border { Child = line };
                host.Measure(new Size(double.PositiveInfinity, 300));
                host.Arrange(new Rect(host.DesiredSize)); host.UpdateLayout();
                var colors = (FrameworkElement)window.FindName("InlineStrokeOptions");
                var choices = (FrameworkElement)window.FindName("InlineToolAdjustments");
                var colorCenter = colors.TranslatePoint(new Point(0, colors.ActualHeight / 2), line).Y;
                var choiceCenter = choices.TranslatePoint(new Point(0, choices.ActualHeight / 2), line).Y;
                if (wrap) Assert.True(choiceCenter > colorCenter + 20);
                else Assert.InRange(Math.Abs(choiceCenter - colorCenter), 0, 1);
                Assert.InRange(line.DesiredSize.Width, 1, width);
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData(150, 218)]
    [InlineData(650, 242)]
    public void ColorPopupPrefersBelowAndFallsBackAboveWhenNeeded(double toolbarTop, double expectedTop)
    {
        var placement = ToolbarPopupPlacement.Below(new Rect(900, toolbarTop, 20, 60), new Size(300, 400), new Rect(8, 8, 1008, 752));
        Assert.Equal(expectedTop, placement.Y);
        Assert.Equal(716, placement.X);
    }

    [Theory]
    [InlineData(200, true)]
    [InlineData(850, false)]
    public void CaptureColorPanelUsesTheAvailableSpaceAroundTheActualToolbar(double top, bool below)
    {
        WpfTestHost.Invoke(() =>
        {
            using var window = new CaptureOverlayWindow(null, isStartupPrewarm: true, deferInitialScreenCapture: true);
            try
            {
                var root = (FrameworkElement)window.Content;
                window.Content = null;
                root.Resources = window.Resources;
                using var source = new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters("Hidden color panel placement")
                    { WindowStyle = unchecked((int)0x80000000), Width = 1200, Height = 1100 });
                source.RootVisual = root;
                var toolbar = (Border)window.FindName("CaptureToolbar");
                toolbar.Visibility = Visibility.Visible;
                ((RadioButton)window.FindName("InlineShapeToolButton")).IsChecked = true;
                Canvas.SetLeft(toolbar, 20); Canvas.SetTop(toolbar, top);
                root.Measure(new Size(1200, 1100)); root.Arrange(new Rect(0, 0, 1200, 1100)); root.UpdateLayout();
                Call(window, "ShowInlineCustomColorPanel");
                var picker = (Border)window.FindName("InlineCustomColorPanel");
                var y = Canvas.GetTop(picker);
                Assert.Equal(Visibility.Visible, picker.Visibility);
                if (below) Assert.True(y >= top + toolbar.ActualHeight);
                else Assert.True(y + picker.ActualHeight <= top);
                Assert.InRange(y, 8, 1100 - picker.ActualHeight - 8);
                Assert.False(window.IsVisible);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void HiddenEmojiDefersRasterizationUntilLayoutAndRefreshesChangedSticker()
    {
        WpfTestHost.Invoke(() =>
        {
            var icon = new EmojiStickerImage { Width = 22, Height = 22 };
            Assert.Null(icon.Source);
            icon.Measure(new Size(22, 22)); icon.Arrange(new Rect(0, 0, 22, 22));
            var first = icon.Source;
            Assert.NotNull(first);
            icon.Sticker = "❤️";
            icon.Measure(new Size(22, 22));
            Assert.NotNull(icon.Source);
            Assert.NotSame(first, icon.Source);
        });
    }

    [Theory]
    [InlineData(AppTheme.GlassInk)]
    [InlineData(AppTheme.AuroraMist)]
    public void CaptureStartsUnselectedAndShowsOnlyTheChosenToolsOptions(AppTheme theme)
    {
        WpfTestHost.Invoke(() =>
        {
            var preferences = AnnotationToolPreferences.Snapshot();
            using var window = new CaptureOverlayWindow(null, isStartupPrewarm: true, deferInitialScreenCapture: true);
            try
            {
                AppThemeManager.ApplySettingsPalette(window.Resources, theme);
                var toolbar = (Border)window.FindName("CaptureToolbar");
                var options = (StackPanel)window.FindName("InlineEditorOptions");
                var row = (ToolbarOptionsRow)window.FindName("InlineToolAdjustments");
                var canvas = (ImageEditorCanvas)window.FindName("InlineEditorCanvas");
                Assert.Equal(Visibility.Collapsed, options.Visibility);
                Assert.Empty(row.Children);
                Assert.Null(window.FindName("InlineSharedColorPicker"));
                Assert.Empty(((Button)window.FindName("TranslateButton")).ContextMenu.Items);
                var shape = (RadioButton)window.FindName("InlineShapeToolButton");
                Assert.False(shape.IsChecked);
                RenderToolbar(toolbar, $"capture-initial-{theme}");
                shape.IsChecked = true;
                Assert.Equal(Visibility.Visible, options.Visibility);
                Assert.Equal(2, row.Children.Count);
                ((ToggleButton)row.Children[1]).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Assert.Equal("Ellipse", shape.Tag);
                var arrow = (RadioButton)window.FindName("InlineArrowToolButton");
                arrow.IsChecked = true;
                Assert.Equal(4, row.Children.Count);
                ((ToggleButton)row.Children[3]).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Assert.Equal("CurvedArrow", arrow.Tag);
                var text = (RadioButton)window.FindName("InlineTextToolButton");
                text.IsChecked = true;
                Assert.Equal(2, row.Children.Count);
                ((ToggleButton)row.Children[1]).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Assert.True(canvas.TextBackgroundEnabled);
                ((RadioButton)window.FindName("InlineMosaicToolButton")).IsChecked = true;
                Assert.Equal(2, row.Children.Count);
                ((ToggleButton)row.Children[1]).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Assert.Equal("RectangleMosaic", ((RadioButton)window.FindName("InlineMosaicToolButton")).Tag);
                var brush = (RadioButton)window.FindName("InlineBrushToolButton");
                brush.IsChecked = true;
                Assert.Empty(row.Children);
                var colorPicker = typeof(CaptureOverlayWindow).GetProperty("InlineSharedColorPicker", Private)!.GetValue(window);
                Assert.IsType<SharedColorPickerControl>(colorPicker);
                Assert.Same(colorPicker, ((ContentControl)window.FindName("InlineSharedColorHost")).Content);
                Assert.Same(colorPicker, typeof(CaptureOverlayWindow).GetProperty("InlineSharedColorPicker", Private)!.GetValue(window));
                Assert.Equal(Visibility.Visible, ((StackPanel)window.FindName("InlineStrokeOptions")).Visibility);
                var number = (RadioButton)window.FindName("InlineNumberToolButton");
                number.IsChecked = true;
                var numberPanel = Assert.IsType<StackPanel>(Assert.Single(row.Children.Cast<UIElement>()));
                var format = Assert.IsType<ComboBox>(numberPanel.Children[0]);
                format.SelectedValue = NumberingStyle.UpperLetters;
                ((RepeatButton)numberPanel.Children[3]).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Assert.Equal(NumberingStyle.UpperLetters, canvas.GetNumberOptions().Style);
                Assert.Equal(2, canvas.GetNumberOptions().Value);
                arrow.IsChecked = true;
                RenderToolbar(toolbar, $"capture-{theme}");
                Call(window, "ClearInlineToolSelection");
                Assert.Equal(Visibility.Collapsed, options.Visibility);
                Assert.Empty(row.Children);
                ((Button)window.FindName("TranslateButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(Visibility.Collapsed, ((ToolbarOptionsRow)window.FindName("InlineActionAdjustments")).Visibility);
                RenderToolbar(toolbar, $"capture-translation-{theme}");
            }
            finally { window.Close(); AnnotationToolPreferences.Configure(preferences, null); }
        });
    }

    [Fact]
    public void RecordingShowsOptionsUnderTheToolsAndHidesThemInPointerMode()
    {
        WpfTestHost.Invoke(() =>
        {
            var region = new ScreenRegion(30, 30, 320, 240);
            using var recorder = new RegionVideoRecorder(region, Path.GetTempPath());
            var input = new RecordingInputOverlayWindow(region);
            var annotation = new RecordingAnnotationOverlayWindow(region);
            var window = (VideoRecordingControlWindow)Activator.CreateInstance(typeof(VideoRecordingControlWindow), true)!;
            try
            {
                typeof(VideoRecordingControlWindow).GetMethod("InitializeSession", Private)!.Invoke(window,
                [recorder, Path.GetTempPath(), false, false, VideoRecordingCodec.H264, 30, false, false, false, false,
                    null, null, VideoRecordingOutputFormat.Mp4, input, annotation, null,
                    new VideoRecordingAnnotationPreferences(ShapeToolMode.Rectangle, ArrowToolMode.Straight, ArrowStyle.Filled, "#F04455", 3),
                    null, null, null, "Alt+2", 100d, null, null]);
                AppThemeManager.ApplySettingsPalette(window.Resources, AppTheme.GlassInk);
                var arrowIcon = (System.Windows.Shapes.Path)window.FindName("ArrowToolIcon");
                Assert.Equal(((SolidColorBrush)window.FindResource("EditorToolbarIconBrush")).Color,
                    Assert.IsType<SolidColorBrush>(arrowIcon.Fill).Color);
                var options = (StackPanel)window.FindName("AnnotationPropertiesPanel");
                var row = (ToolbarOptionsRow)window.FindName("RecordingToolAdjustments");
                Assert.Equal(Visibility.Collapsed, options.Visibility);
                Call(window, "ApplySelectedAnnotationToolStyle", RecordingAnnotationTool.Rectangle);
                Assert.Equal(Visibility.Visible, options.Visibility);
                Assert.Equal(2, row.Children.Count);
                Assert.True(row.CycleChoice());
                Assert.Equal("Ellipse", ((ToggleButton)window.FindName("ShapeToolButton")).Tag);
                Assert.Equal(window.FindResource("EllipseIconGeometry"), ((System.Windows.Shapes.Path)window.FindName("ShapeToolIcon")).Data);
                Call(window, "ApplySelectedAnnotationToolStyle", RecordingAnnotationTool.Arrow);
                Assert.Equal(4, row.Children.Count);
                var shortcut = new ToolbarVariantShortcut();
                for (var i = 0; i < 4; i++)
                {
                    shortcut.KeyDown(System.Windows.Input.Key.RightShift, System.Windows.Input.ModifierKeys.Shift, false, row);
                    Assert.True(shortcut.KeyUp(System.Windows.Input.Key.RightShift, System.Windows.Input.ModifierKeys.None, row));
                    Assert.True(((ToggleButton)row.Children[(i + 1) % 4]).IsChecked);
                }
                ((FrameworkElement)window.FindName("AnnotationToolsPanel")).Visibility = Visibility.Visible;
                RenderToolbar((Border)window.FindName("ControlSurface"), "recording-GlassInk");
                Call(window, "ApplySelectedAnnotationToolStyle", RecordingAnnotationTool.Pointer);
                Assert.Equal(Visibility.Collapsed, options.Visibility);
                Assert.Empty(row.Children);
                Call(window, "ApplySelectedAnnotationToolStyle", RecordingAnnotationTool.Emoji);
                var palette = Assert.IsType<ToolbarEmojiStrip>(Assert.Single(row.Children.Cast<UIElement>()));
                Assert.Equal(102, palette.MaxHeight);
                Assert.Equal(EmojiStickerCatalog.All.Count, ((WrapPanel)palette.Content).Children.Count);
                Assert.Equal(1, ((Panel)window.FindName("RecordingAdjustmentLine")).Children.IndexOf(options));
                RenderToolbar((Border)window.FindName("ControlSurface"), "recording-emoji-GlassInk");
                Assert.Equal(Visibility.Collapsed, ((Button)window.FindName("AnnotationCyanColorButton")).Visibility);
                Assert.Equal(RegionVideoRecorderState.Ready, recorder.State);
                Assert.False(window.IsVisible);
                Assert.False(annotation.IsVisible);
            }
            finally { window.Close(); input.Close(); annotation.Close(); }
        });
    }

    internal static void RenderToolbar(Border toolbar, string name)
    {
        if (Environment.GetEnvironmentVariable("SNAPCUT_TOOLBAR_PREVIEW_DIRECTORY") is not { Length: > 0 } directory) return;
        // Layout/render only: no Show, screen capture or injected mouse input.
        var resources = Window.GetWindow(toolbar)?.Resources ?? toolbar.Resources;
        var oldParent = toolbar.Parent;
        var oldIndex = oldParent is Panel oldPanel ? oldPanel.Children.IndexOf(toolbar) : 0;
        var visibility = toolbar.Visibility;
        var opacity = toolbar.Opacity;
        if (toolbar.Parent is Panel parent) parent.Children.Remove(toolbar);
        else if (toolbar.Parent is Decorator decorator) decorator.Child = null;
        toolbar.Visibility = Visibility.Visible;
        toolbar.Opacity = 1;
        var host = new Border { Background = Brushes.DimGray, Child = toolbar };
        if (resources is not null) host.Resources = resources;
        host.Measure(new Size(1600, 700));
        host.Arrange(new Rect(host.DesiredSize));
        host.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(host.ActualWidth), (int)Math.Ceiling(host.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(host);
        Directory.CreateDirectory(directory);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(directory, name + ".png")); encoder.Save(stream);
        host.Child = null;
        if (oldParent is Panel restorePanel) restorePanel.Children.Insert(oldIndex, toolbar);
        else if (oldParent is Decorator restoreDecorator) restoreDecorator.Child = toolbar;
        toolbar.Visibility = visibility;
        toolbar.Opacity = opacity;
    }

    private static void Call(object target, string method, params object[] arguments) =>
        target.GetType().GetMethod(method, Private)!.Invoke(target, arguments);
}
