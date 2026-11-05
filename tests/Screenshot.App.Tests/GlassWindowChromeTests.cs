using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using Screenshot.App.Core;
using Screenshot.App.Infrastructure;
using Screenshot.App.Presentation;

namespace Screenshot.App.Tests;

public sealed class GlassWindowChromeTests
{
    [Theory]
    [InlineData(100, 34)]
    [InlineData(260, 42)]
    public void LiquidControlRimLeavesTextAndDesktopCentreUntouched(int width, int height)
    {
        WpfTestHost.Invoke(() =>
        {
            var lens = new LiquidGlassLens();
            lens.Measure(new Size(width, height));
            lens.Arrange(new Rect(0, 0, width, height));
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(lens);
            var pixels = new byte[width * height * 4];
            bitmap.CopyPixels(pixels, width * 4, 0);
            Assert.False(lens.IsHitTestVisible);
            Assert.Equal(0, pixels[((height / 2 * width) + width / 2) * 4 + 3]);
            Assert.Contains(pixels.Skip(3).Where((_, index) => index % 4 == 0), alpha => alpha > 100);
        });
    }

    [Fact]
    public void LongThemeTooltipsWrapInsteadOfClippingAtTheirWidthLimit()
    {
        WpfTestHost.Invoke(() =>
        {
            var appPath = System.IO.Path.GetFullPath("../../../../../src/Screenshot.App/App.xaml", AppContext.BaseDirectory);
            var app = System.Xml.Linq.XDocument.Load(appPath);
            var dictionary = app.Root!.Elements().Single(element => element.Name.LocalName == "Application.Resources").Elements().Single();
            dictionary.SetAttributeValue(System.Xml.Linq.XNamespace.Xmlns + "x", "http://schemas.microsoft.com/winfx/2006/xaml");
            var resources = (ResourceDictionary)System.Windows.Markup.XamlReader.Parse(dictionary.ToString().Replace(
                "clr-namespace:Screenshot.App.Presentation", "clr-namespace:Screenshot.App.Presentation;assembly=SnapCut", StringComparison.Ordinal),
                new System.Windows.Markup.ParserContext { BaseUri = new Uri("pack://application:,,,/SnapCut;component/") });
            var tip = new ToolTip
            {
                Content = "深色 · 自绘蓝紫柔光、玻璃弧面与指针高光；比普通主题有额外图形开销",
                Style = (Style)resources[typeof(ToolTip)],
            };
            tip.Resources.MergedDictionaries.Add(resources);
            tip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            tip.Arrange(new Rect(tip.DesiredSize));
            tip.UpdateLayout();
            var presenter = Assert.IsType<ContentPresenter>(Assert.IsType<Border>(VisualTreeHelper.GetChild(tip, 0)).Child);
            var text = Assert.IsType<TextBlock>(VisualTreeHelper.GetChild(presenter, 0));
            Assert.Equal(TextWrapping.Wrap, text.TextWrapping);
            Assert.Equal(tip.Content, text.Text);
            Assert.InRange(tip.ActualWidth, 250, 360);
            Assert.InRange(text.ActualHeight, 28, 45); // Two lines at the authored font size.
            Assert.False(tip.IsOpen);
        });
    }

    [Fact]
    public void HistoryHeaderAndTrayCanBeRenderedWithoutShowingDesktopWindows()
    {
        WpfTestHost.Invoke(() =>
        {
            var missingDirectory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"snapcut-chrome-{Guid.NewGuid():N}");
            var history = new Screenshot.App.Capture.CaptureHistoryWindow(
                new Screenshot.App.Capture.CaptureHistoryService(missingDirectory), missingDirectory, missingDirectory);
            using var tray = new TrayIconService(AppTheme.GlassMist);
            var menu = tray.CreateGlassMenu();
            try
            {
                AppThemeManager.ApplySettingsPalette(history.Resources, AppTheme.GlassMist);
                _ = new WindowInteropHelper(history).EnsureHandle();
                LiquidGlassChrome.Apply(history, AppTheme.GlassMist);
                _ = new WindowInteropHelper(menu).EnsureHandle();
                var host = new Grid { Width = 940, Height = 560, Background = Brushes.SlateGray };
                foreach (var (window, width, left, top) in new[] { ((Window)history, 680d, 0d, 0d), (menu, 210d, 710d, 125d) })
                {
                    var surface = (FrameworkElement)window.Content;
                    window.Content = null;
                    surface.Resources.MergedDictionaries.Add(window.Resources);
                    surface.Width = width;
                    surface.Height = window == history ? 560 : double.NaN;
                    surface.HorizontalAlignment = HorizontalAlignment.Left;
                    surface.VerticalAlignment = VerticalAlignment.Top;
                    surface.Margin = new Thickness(left, top, 0, 0);
                    host.Children.Add(surface);
                }
                host.Measure(new Size(940, 560));
                host.Arrange(new Rect(0, 0, 940, 560));
                host.UpdateLayout();
                var bitmap = new RenderTargetBitmap(940, 560, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(host);
                Assert.All(new Window[] { history, menu }, window => Assert.False(window.IsVisible));
                if (Environment.GetEnvironmentVariable("SNAPCUT_CHROME_PREVIEW") is { Length: > 0 } output)
                {
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var stream = System.IO.File.Create(output);
                    encoder.Save(stream);
                }
            }
            finally { menu.Close(); history.Close(); }
        });
    }

    [Fact]
    public void HistoryPreviewDoesNotStayAboveTheHistoryList()
    {
        WpfTestHost.Invoke(() =>
        {
            using var image = new Screenshot.App.Capture.CapturedImage(new System.Drawing.Bitmap(20, 20));
            var preview = new Screenshot.App.Capture.CapturePreviewWindow(image, System.IO.Path.GetTempPath(), null);
            try
            {
                Assert.True(preview.Topmost);
                preview.ConfigureForHistoryView();
                Assert.False(preview.Topmost);
                Assert.True(preview.ShowInTaskbar);
                Assert.False(preview.IsVisible);
            }
            finally { preview.Close(); }
        });
    }

    [Fact]
    public void GlassCaptionKeepsThreeVisibleHitTargetsAndWorkingClose()
    {
        WpfTestHost.Invoke(() =>
        {
            var window = new Window { Width = 680, Height = 420, Content = new Grid() };
            AppThemeManager.ApplySettingsPalette(window.Resources, AppTheme.GlassMist);
            try
            {
                WindowShell.Attach(window, "历史查看");
                var frame = Assert.IsType<Border>(window.Content);
                frame.Measure(new Size(680, 420));
                frame.Arrange(new Rect(0, 0, 680, 420));
                frame.UpdateLayout();
                var header = Assert.IsType<Border>(Assert.IsType<Grid>(frame.Child).Children[0]);
                Assert.InRange(Assert.IsType<SolidColorBrush>(header.Background).Color.A, 1, 35);
                var actions = Assert.IsType<StackPanel>(Assert.IsType<DockPanel>(header.Child).Children[0]);
                Assert.Equal(3, actions.Children.Count);
                var buttons = actions.Children.OfType<Button>().ToArray();
                Assert.All(buttons, button =>
                {
                    Assert.True(button.ActualWidth >= 40 && button.ActualHeight >= 40);
                    Assert.True(WindowChrome.GetIsHitTestVisibleInChrome(button));
                    var label = Assert.IsType<TextBlock>(button.Content);
                    // The explicit caption text participates in glass contrast protection.
                    ClearGlassText.Apply(label, true);
                    Assert.Equal(Colors.White, Assert.IsType<SolidColorBrush>(label.Foreground).Color);
                    Assert.NotNull(label.Effect);
                });
                var bitmap = new RenderTargetBitmap(680, 420, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(frame);
                Assert.False(window.IsVisible);
                var closed = false;
                window.Closed += (_, _) => closed = true;
                buttons[^1].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.True(closed);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void GlassTrayCommandsMatchNativeMenuAndCloseBeforeDispatch()
    {
        WpfTestHost.Invoke(() =>
        {
            using var tray = new TrayIconService(AppTheme.GlassMist);
            var menu = tray.CreateGlassMenu();
            try
            {
                var labels = tray.ContextMenuForTesting.Items.OfType<System.Windows.Forms.ToolStripMenuItem>()
                    .Where(item => item.Available).Select(item => item.Text);
                Assert.Equal(labels, menu.Commands.Select(button => (string)button.Content));
                _ = new WindowInteropHelper(menu).EnsureHandle();
                var surface = Assert.IsType<Border>(menu.Content);
                surface.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                surface.Arrange(new Rect(surface.DesiredSize));
                surface.UpdateLayout();
                Assert.False(menu.IsVisible);
                Assert.False(menu.ShowInTaskbar);
                Assert.Equal(1, menu.Opacity);
                Assert.All(Assert.IsType<LinearGradientBrush>(surface.Background).GradientStops,
                    stop => Assert.InRange(stop.Color.A, 185, 225));
                var closed = false;
                var requested = false;
                menu.Closed += (_, _) => closed = true;
                tray.HistoryRequested += (_, _) => { Assert.True(closed); requested = true; };
                menu.Commands.Single(button => Equals(button.Content, "历史查看"))
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.True(requested);
            }
            finally { menu.Close(); }
        });
    }

    [Theory]
    [InlineData(1910, 1070, 0, 0, 1920, 1040, 225, 340)]
    [InlineData(-1880, 10, -1920, 0, 1920, 1080, 300, 470)]
    [InlineData(3830, -1050, 1920, -1080, 1920, 1080, 250, 390)]
    public void TrayMenuStaysInsideTheClickedMonitorsWorkArea(int x, int y, int left, int top,
        int width, int height, int menuWidth, int menuHeight)
    {
        var work = new System.Drawing.Rectangle(left, top, width, height);
        var location = GlassTrayMenuWindow.ConstrainToWorkArea(new System.Drawing.Point(x, y), work, menuWidth, menuHeight);
        Assert.True(work.Contains(new System.Drawing.Rectangle(location, new System.Drawing.Size(menuWidth, menuHeight))));
    }
}
