using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using Screenshot.App.Core;
using Screenshot.App.Infrastructure;
using Screenshot.App.Presentation;

namespace Screenshot.App.Tests;

public sealed class GlassSelectionAndReliefTests
{
    private static readonly string[] NeutralSurfaces = ["AppGlassBackgroundBrush", "AppPanelBackgroundBrush", "AppInputBackgroundBrush",
        "AppPopupBackgroundBrush", "AppFloatingSurfaceBrush", "AppAccentBrush", "EditorToolbarButtonSelectedBackgroundBrush"];

    [Theory]
    [InlineData("Capture/CaptureOverlayWindow.xaml", "InlineToolButton", true)]
    [InlineData("Capture/CaptureOverlayWindow.xaml", "InlineActionButton", false)]
    [InlineData("Pin/PinnedImageEditorToolbarWindow.xaml", "ToolbarToggle", true)]
    [InlineData("Pin/PinnedImageEditorToolbarWindow.xaml", "ToolbarButton", false)]
    [InlineData("Editor/ImageEditorWindow.xaml", "GlassTool", true)]
    [InlineData("Editor/ImageEditorWindow.xaml", "GlassButton", false)]
    public void ActualToolbarButtonsRenderTheSameGlassAsTheLivePreview(string file, string key, bool selectable)
    {
        WpfTestHost.Invoke(() =>
        {
            var resources = ReadApplicationStyles();
            AppThemeManager.ApplySettingsPalette(resources, AppTheme.GlassMist);
            var preview = new Button { Style = (Style)resources[typeof(Button)] };
            Control actual = selectable ? new RadioButton() : new Button();
            actual.Style = ReadStyle(file, key);
            foreach (var button in new[] { preview, actual })
            {
                button.Resources = resources;
                button.MinWidth = 0; button.Width = 36; button.Height = 32;
                button.Margin = new Thickness(0); button.Padding = new Thickness(0);
                button.SetResourceReference(Control.BackgroundProperty, "EditorToolbarButtonBackgroundBrush");
                button.SetResourceReference(Control.BorderBrushProperty, "EditorToolbarButtonBorderBrush");
                button.SetResourceReference(Control.ForegroundProperty, "EditorToolbarIconBrush");
                Layout(button, 36, 32);
            }
            static byte[] Pixels(Visual visual)
            {
                var bitmap = new RenderTargetBitmap(36, 32, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(visual);
                var pixels = new byte[36 * 32 * 4];
                bitmap.CopyPixels(pixels, 36 * 4, 0);
                return pixels;
            }
            Assert.Equal(Pixels(preview), Pixels(actual));
            if (actual is RadioButton radio)
            {
                var idle = Pixels(actual);
                radio.IsChecked = true;
                actual.UpdateLayout();
                Assert.NotEqual(idle, Pixels(actual));
            }
        });
    }

    public static IEnumerable<object[]> ThemedSelectionStyles()
    {
        var themes = new[] { AppTheme.AuroraMist, AppTheme.CoralSky, AppTheme.GinkgoPaper, AppTheme.ForestNight,
            AppTheme.ObsidianGold, AppTheme.NeonDeep, AppTheme.GlassMist, AppTheme.GlassInk };
        var styles = new[]
        {
            ("Presentation/MainWindow.xaml", "ThemeOptionButton"),
            ("Capture/CaptureOverlayWindow.xaml", "InlineToolButton"),
            ("Pin/PinnedImageEditorToolbarWindow.xaml", "ToolbarToggle"),
            ("Editor/ImageEditorWindow.xaml", "GlassTool"),
            ("Capture/CaptureHistoryWindow.xaml", "HistoryViewSwitch"),
            ("Capture/VideoRecordingControlWindow.xaml", "RecordingToolButton"),
        };
        foreach (var theme in themes)
            foreach (var (file, key) in styles)
                yield return [file, key, theme];
    }

    [Theory]
    [MemberData(nameof(ThemedSelectionStyles))]
    public void AuthoredTabAndToolStylesUseATravellingBackgroundWithoutMovingInput(string file, string key, AppTheme theme)
    {
        WpfTestHost.Invoke(() =>
        {
            var group = new StackPanel { Orientation = Orientation.Horizontal, Background = Brushes.Transparent };
            AppThemeManager.ApplySettingsPalette(group.Resources, theme);
            var style = ReadStyle(file, key);
            var first = new RadioButton { Content = "A", Style = style, Width = 100, IsChecked = true, GroupName = "test" };
            var second = new RadioButton { Content = "B", Style = style, Width = 120, GroupName = "test" };
            group.Children.Add(first);
            group.Children.Add(second);
            // Toolbar ink is authored against its real glass board. Omitting
            // that layer makes contrast protection correctly choose white ink.
            var shell = new Border { Child = group };
            if (key is "InlineToolButton" or "ToolbarToggle" or "GlassTool" or "RecordingToolButton")
                shell.Background = (Brush)group.Resources["AppFloatingSurfaceBrush"];
            Layout(shell, 300, 60);
            SelectionMotion.Register(first);
            SelectionMotion.Register(second);
            try
            {
                var bounds = SelectionMotion.CurrentBounds(first);
                Assert.True(SelectionMotion.GetIsHosted(first));
                // The selected plate is drawn by the parent. Its transparent
                // button template must still retain dark, readable lettering.
                if (theme == AppTheme.GlassMist)
                {
                    var selectedText = Descendants<TextBlock>(first).Single(text => text.Text == "A");
                    ClearGlassText.Apply(selectedText, true);
                    Assert.True(Assert.IsType<SolidColorBrush>(selectedText.Foreground).Color.R < 80);
                }
                Assert.Equal(theme == AppTheme.GlassMist ? Visibility.Visible : Visibility.Collapsed,
                    group.Resources["AppLiquidLensVisibility"]);
                Assert.InRange(bounds.Width, first.ActualWidth - 2, first.ActualWidth);
                var originalPosition = second.TranslatePoint(new Point(), group);
                var originalSize = second.RenderSize;
                SelectionMotion.Move(second, true);
                Assert.Equal(bounds, SelectionMotion.CurrentBounds(second));
                Pump(65);
                var between = SelectionMotion.CurrentBounds(second);
                Assert.InRange(between.X, bounds.X + .1, originalPosition.X + 1);
                // WPF advances its shared animation clock on render ticks.
                // A busy test dispatcher may finish the transition by this
                // sample even when the local wait was short. Both an interior
                // position and the target are valid; input geometry is fixed.
                Assert.Equal(originalPosition, second.TranslatePoint(new Point(), group));
                Assert.Equal(originalSize, second.RenderSize);
                Assert.True(second.RenderTransform.Value.IsIdentity);

                // Reversal starts where the travelling plate currently is.
                SelectionMotion.Move(first, true);
                var reversed = SelectionMotion.CurrentBounds(first);
                Assert.InRange(Math.Abs(reversed.X - between.X), 0, 2);
                Pump(270);
                Assert.Equal(bounds, SelectionMotion.CurrentBounds(first));

                var commands = 0;
                second.Checked += (_, _) => commands++;
                second.IsChecked = true;
                Assert.Equal(1, commands); // Tool switching never waits for animation.
                SelectionMotion.Move(second, false);
                Assert.InRange(SelectionMotion.CurrentBounds(second).X, originalPosition.X, originalPosition.X + 1);

                AppThemeManager.ApplySettingsPalette(group.Resources, AppTheme.AuroraMist);
                SelectionMotion.Register(first);
                SelectionMotion.Register(second);
                Assert.True(SelectionMotion.GetIsHosted(first));
                Assert.True(SelectionMotion.GetIsHosted(second));
                Assert.InRange(SelectionMotion.CurrentBounds(second).X, originalPosition.X, originalPosition.X + 1);

                // Disabling selection hosting still restores the panel and
                // detaches its animation; changing material must not disable it.
                group.Resources["AppSelectionMotionVisibility"] = Visibility.Collapsed;
                SelectionMotion.Register(first);
                SelectionMotion.Register(second);
                Assert.False(SelectionMotion.GetIsHosted(first));
                Assert.False(SelectionMotion.GetIsHosted(second));
                Assert.Same(Brushes.Transparent, group.Background);
                Assert.True(SelectionMotion.CurrentBounds(second).IsEmpty);
            }
            finally
            {
                SelectionMotion.Unregister(first);
                SelectionMotion.Unregister(second);
            }
        });
    }

    [Theory]
    [InlineData(AppTheme.AuroraMist)]
    [InlineData(AppTheme.CoralSky)]
    [InlineData(AppTheme.GinkgoPaper)]
    [InlineData(AppTheme.ForestNight)]
    [InlineData(AppTheme.ObsidianGold)]
    [InlineData(AppTheme.NeonDeep)]
    [InlineData(AppTheme.GlassMist)]
    [InlineData(AppTheme.GlassInk)]
    public void NavigationPlateMovesVerticallyAndSnapsAfterResize(AppTheme theme)
    {
        WpfTestHost.Invoke(() =>
        {
            var group = new StackPanel { Width = 180 };
            AppThemeManager.ApplySettingsPalette(group.Resources, theme);
            var style = ReadStyle("Presentation/MainWindow.xaml", "NavigationItem");
            var first = new ListBoxItem { Content = "常规设置", Style = style, IsSelected = true };
            var second = new ListBoxItem { Content = "版本更新", Style = style, Tag = "Maintenance" };
            group.Children.Add(first);
            group.Children.Add(second);
            Layout(group, 180, 200);
            SelectionMotion.Register(first);
            SelectionMotion.Register(second);
            try
            {
                SelectionMotion.Move(second, true);
                Pump(60);
                Assert.InRange(SelectionMotion.CurrentBounds(second).Y, 3, 80);
                first.IsSelected = false;
                second.IsSelected = true;
                group.Width = 220;
                Layout(group, 220, 200);
                Assert.InRange(SelectionMotion.CurrentBounds(second).Width, 218, 220);
                Assert.InRange(SelectionMotion.CurrentBounds(second).Y, 60, 110);
                Assert.True(first.RenderTransform.Value.IsIdentity);
                Assert.True(second.RenderTransform.Value.IsIdentity);
            }
            finally { SelectionMotion.Unregister(first); SelectionMotion.Unregister(second); }
        });
    }

    [Fact]
    public void TranslatedTabTextResizesTheSelectionPlateInsideAnUnchangedPanel()
    {
        WpfTestHost.Invoke(() =>
        {
            var group = new WrapPanel { Width = 600 };
            AppThemeManager.ApplySettingsPalette(group.Resources, AppTheme.GlassInk);
            var tab = new RadioButton { Content = "外观与保存", IsChecked = true,
                Style = ReadStyle("Presentation/MainWindow.xaml", "ThemeOptionButton") };
            group.Children.Add(tab);
            Layout(group, 600, 60);
            SelectionMotion.Register(tab);
            try
            {
                var before = SelectionMotion.CurrentBounds(tab).Width;
                tab.Content = "Appearance and saving";
                Layout(group, 600, 60);
                var after = SelectionMotion.CurrentBounds(tab).Width;
                Assert.True(after > before + 20);
                Assert.InRange(after, tab.ActualWidth - 2, tab.ActualWidth);
            }
            finally { SelectionMotion.Unregister(tab); }
        });
    }

    [Fact]
    public void MiddleTabsKeepMovingWhenThePageScrollbarChangesTheAvailableWidth()
    {
        WpfTestHost.Invoke(() =>
        {
            var group = new WrapPanel { Width = 470 };
            AppThemeManager.ApplySettingsPalette(group.Resources, AppTheme.GlassMist);
            var style = ReadStyle("Presentation/MainWindow.xaml", "ThemeOptionButton");
            var tabs = Enumerable.Range(0, 4).Select(index => new RadioButton
            { Content = $"Page {index}", Width = 100, Style = style, GroupName = "Pages", IsChecked = index == 0 }).ToArray();
            foreach (var tab in tabs) group.Children.Add(tab);
            Layout(group, 470, 50);
            foreach (var tab in tabs) SelectionMotion.Register(tab);
            try
            {
                for (var selected = 1; selected <= 2; selected++)
                {
                    tabs[selected - 1].IsChecked = false;
                    tabs[selected].IsChecked = true;
                    SelectionMotion.Move(tabs[selected - 1], false);
                    var origin = SelectionMotion.CurrentBounds(tabs[selected]);
                    SelectionMotion.Move(tabs[selected], true);
                    group.Width = selected == 1 ? 487 : 470;
                    Layout(group, group.Width, 50);
                    Assert.Equal(origin, SelectionMotion.CurrentBounds(tabs[selected]));
                    Pump(270);
                    var end = tabs[selected].TranslatePoint(new Point(), group);
                    Assert.InRange(SelectionMotion.CurrentBounds(tabs[selected]).X, end.X, end.X + 1);
                }
            }
            finally { foreach (var tab in tabs) SelectionMotion.Unregister(tab); }
        });
    }

    [Fact]
    public void DarkGlyphsAndInputTextHaveReliefWithoutRecolouringOrChangingOtherThemes()
    {
        WpfTestHost.Invoke(() =>
        {
            var label = new TextBlock { Text = "Alt+A", Foreground = Brushes.Indigo };
            var root = new Border { Background = Brushes.White, Child = label };
            ClearGlassText.Apply(label, true);
            Assert.Same(Brushes.Indigo, label.Foreground);
            var relief = Assert.IsType<DropShadowEffect>(label.Effect);
            Assert.Equal(Colors.White, relief.Color);
            Assert.InRange(relief.ShadowDepth, .5, 1);
            Assert.Equal(1, label.Opacity);
            ClearGlassText.Apply(label, false);
            Assert.Same(Brushes.Indigo, label.Foreground);
            Assert.Null(label.Effect);

            var glyph = new System.Windows.Shapes.Path { Stroke = Brushes.Indigo, Data = Geometry.Parse("M 1,9 L 9,1") };
            ClearGlassRelief.ApplyControlPart(glyph, true);
            Assert.Same(relief, glyph.Effect); // Shared frozen effect, no per-glyph shader allocation.
            Assert.True(relief.IsFrozen);
            ClearGlassRelief.ApplyControlPart(glyph, false);
            Assert.Null(glyph.Effect);
            var authored = new DropShadowEffect { Color = Colors.Red };
            glyph.Effect = authored;
            ClearGlassRelief.ApplyControlPart(glyph, true);
            ClearGlassRelief.ApplyControlPart(glyph, false);
            Assert.Same(authored, glyph.Effect);
            GC.KeepAlive(root);
        });
    }

    [Fact]
    public void RetentionDialogGetsGlassBodyCaptionAndReadableControlsOffScreen()
    {
        WpfTestHost.Invoke(() =>
        {
            var owner = new Window();
            _ = new WindowInteropHelper(owner).EnsureHandle();
            var dialog = new HistoryRetentionDialog(owner, "截图历史", 7, 50);
            var original = dialog.Content;
            var originalStyle = dialog.WindowStyle;
            try
            {
                dialog.Resources.MergedDictionaries.Add(ReadApplicationStyles());
                AppThemeManager.ApplySettingsPalette(dialog.Resources, AppTheme.GlassMist);
                _ = new WindowInteropHelper(dialog).EnsureHandle();
                LiquidGlassChrome.Apply(dialog, AppTheme.GlassMist);
                Assert.Equal(WindowStyle.None, dialog.WindowStyle);
                Assert.Same(Brushes.Transparent, dialog.Background);
                var shell = Assert.IsType<Border>(dialog.Content);
                Layout(shell, 470, 340);
                LiquidGlassChrome.Apply(dialog, AppTheme.GlassMist);
                var labels = Descendants<TextBlock>(shell).ToArray();
                Assert.Contains(labels, text => text.Text.EndsWith("设置截图历史保留策略", StringComparison.Ordinal));
                Assert.All(labels.Where(text => !string.IsNullOrEmpty(text.Text)), text => Assert.NotNull(text.Effect));
                var inputs = Descendants<TextBox>(shell).ToArray();
                Assert.Equal(2, inputs.Length);
                foreach (var input in inputs)
                {
                    var content = Assert.IsType<ScrollViewer>(input.Template.FindName("PART_ContentHost", input));
                    Assert.NotNull(content.Effect);
                    Assert.Equal(1, input.Opacity);
                }
                if (Environment.GetEnvironmentVariable("SNAPCUT_DIALOG_PREVIEW") is { Length: > 0 } path)
                {
                    dialog.Content = null;
                    shell.Resources.MergedDictionaries.Add(dialog.Resources);
                    var backdrop = new Grid { Background = Brushes.SlateGray };
                    backdrop.Children.Add(new TextBlock { Text = "窗口下面的软件内容    Window underneath", Foreground = Brushes.LightSteelBlue,
                        FontSize = 22, Margin = new Thickness(25, 95, 0, 0) });
                    backdrop.Children.Add(shell);
                    Layout(backdrop, 470, 340);
                    var image = new RenderTargetBitmap(470, 340, 96, 96, PixelFormats.Pbgra32);
                    image.Render(backdrop);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(image));
                    using (var stream = File.Create(path)) encoder.Save(stream);
                    backdrop.Children.Remove(shell);
                    dialog.Content = shell;
                }
                AppThemeManager.ApplySettingsPalette(dialog.Resources, AppTheme.AuroraMist);
                LiquidGlassChrome.Apply(dialog, AppTheme.AuroraMist);
                Assert.Same(original, dialog.Content);
                Assert.Equal(originalStyle, dialog.WindowStyle);
                Assert.All(Descendants<TextBlock>((DependencyObject)dialog.Content), text => Assert.Null(text.Effect));
                Assert.False(dialog.IsVisible);
                Assert.Null(dialog.ConfirmationResult); // No retention settings or history touched.
            }
            finally { dialog.Close(); owner.Close(); }
        });
    }

    [Fact]
    public void RecognitionPopupUsesBrightSelectableTextWithoutAnInnerGlassFrame()
    {
        WpfTestHost.Invoke(() =>
        {
            var window = new Screenshot.App.Text.RecognitionResultPopupWindow("文字识别", "可选择、可复制的识别文字。", null);
            try
            {
                window.Resources.MergedDictionaries.Add(ReadApplicationStyles());
                AppThemeManager.ApplySettingsPalette(window.Resources, AppTheme.GlassMist);
                _ = new WindowInteropHelper(window).EnsureHandle();
                LiquidGlassChrome.Apply(window, AppTheme.GlassMist);
                var shell = (FrameworkElement)window.Content;
                Layout(shell, 480, 350);
                LiquidGlassChrome.Apply(window, AppTheme.GlassMist);
                var field = (TextBox)window.FindName("ResultTextBox");
                Assert.Equal(Colors.White, Assert.IsType<SolidColorBrush>(field.Foreground).Color);
                Assert.Empty(Descendants<Border>(field));
                Assert.Empty(Descendants<LiquidGlassLens>(field));
                Assert.NotNull(((ScrollViewer)field.Template.FindName("PART_ContentHost", field)).Effect);
                field.Select(0, 3);
                Assert.Equal("可选择", field.SelectedText);
                Assert.True(field.IsReadOnly);
                if (Environment.GetEnvironmentVariable("SNAPCUT_OCR_PREVIEW") is { Length: > 0 } path)
                {
                    field.Select(0, 0);
                    var image = new RenderTargetBitmap(480, 350, 96, 96, PixelFormats.Pbgra32);
                    var visual = new DrawingVisual();
                    using (var dc = visual.RenderOpen())
                    {
                        dc.DrawRectangle(Brushes.DimGray, null, new Rect(0, 0, 480, 350));
                        dc.DrawRectangle(new VisualBrush(shell), null, new Rect(0, 0, 480, 350));
                    }
                    image.Render(visual);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(image));
                    using var stream = File.Create(path);
                    encoder.Save(stream);
                }
                AppThemeManager.ApplySettingsPalette(window.Resources, AppTheme.AuroraMist);
                LiquidGlassChrome.Apply(window, AppTheme.AuroraMist);
                Assert.Equal(((SolidColorBrush)window.Resources["AppTextPrimaryBrush"]).Color,
                    Assert.IsType<SolidColorBrush>(field.Foreground).Color);
                Assert.False(window.IsVisible);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void ClearGlassSurfacesHaveNeutralTintInsteadOfPurple()
    {
        WpfTestHost.Invoke(() =>
        {
            var resources = new ResourceDictionary();
            AppThemeManager.ApplySettingsPalette(resources, AppTheme.GlassMist);
            foreach (var key in NeutralSurfaces)
            {
                var brush = Assert.IsType<LinearGradientBrush>(resources[key]);
                foreach (var stop in brush.GradientStops)
                {
                    Assert.True(stop.Color.R <= stop.Color.G && stop.Color.G <= stop.Color.B, key);
                    Assert.InRange(stop.Color.B - stop.Color.R, 0, 30);
                }
            }
        });
    }

    private static void Layout(FrameworkElement element, double width, double height)
    {
        element.Measure(new Size(width, height));
        element.Arrange(new Rect(0, 0, width, height));
        element.UpdateLayout();
    }

    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private static Style ReadStyle(string file, string key)
    {
        var source = XDocument.Load(Path.GetFullPath("../../../../../src/Screenshot.App/" + file, AppContext.BaseDirectory));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var style = source.Descendants().Single(node => node.Name.LocalName == "Style" && (string?)node.Attribute(x + "Key") == key);
        var context = new ParserContext();
        context.XmlnsDictionary.Add("", "http://schemas.microsoft.com/winfx/2006/xaml/presentation");
        context.XmlnsDictionary.Add("x", x.NamespaceName);
        context.XmlnsDictionary.Add("local", "clr-namespace:Screenshot.App.Presentation;assembly=SnapCut");
        context.XmlnsDictionary.Add("presentation", "clr-namespace:Screenshot.App.Presentation;assembly=SnapCut");
        return (Style)XamlReader.Parse(style.ToString().Replace("clr-namespace:Screenshot.App.Presentation",
            "clr-namespace:Screenshot.App.Presentation;assembly=SnapCut", StringComparison.Ordinal), context);
    }

    private static ResourceDictionary ReadApplicationStyles()
    {
        var source = XDocument.Load(Path.GetFullPath("../../../../../src/Screenshot.App/App.xaml", AppContext.BaseDirectory));
        var dictionary = source.Root!.Elements().Single(element => element.Name.LocalName == "Application.Resources").Elements().Single();
        dictionary.SetAttributeValue(XNamespace.Xmlns + "x", "http://schemas.microsoft.com/winfx/2006/xaml");
        return (ResourceDictionary)XamlReader.Parse(dictionary.ToString().Replace("clr-namespace:Screenshot.App.Presentation",
            "clr-namespace:Screenshot.App.Presentation;assembly=SnapCut", StringComparison.Ordinal),
            new ParserContext { BaseUri = new Uri("pack://application:,,,/SnapCut;component/") });
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
}
