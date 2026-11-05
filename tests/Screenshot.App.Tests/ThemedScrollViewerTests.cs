using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Screenshot.App.Tests;

public sealed class ThemedScrollViewerTests
{
    [Theory]
    [InlineData(800, 900, true, true)]
    [InlineData(800, 60, true, false)]
    [InlineData(60, 900, false, true)]
    [InlineData(60, 60, false, false)]
    public void ScrollbarsRetainOffsetsWithoutPaintingTheIntersection(
        double contentWidth, double contentHeight, bool horizontal, bool vertical)
    {
        WpfTestHost.Invoke(() =>
        {
            var controls = LoadControls();
            var viewer = new ScrollViewer
            {
                Style = (Style)controls[typeof(ScrollViewer)],
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = new Border { Width = contentWidth, Height = contentHeight, Background = Brushes.DarkSlateBlue },
            };
            var host = new Border { Background = Brushes.MidnightBlue, Child = viewer };
            Layout(host, 240, 160);
            var hbar = Assert.IsType<ScrollBar>(viewer.Template.FindName("PART_HorizontalScrollBar", viewer));
            var vbar = Assert.IsType<ScrollBar>(viewer.Template.FindName("PART_VerticalScrollBar", viewer));
            Assert.Equal(horizontal, hbar.IsVisible || hbar.Visibility == Visibility.Visible);
            Assert.Equal(vertical, vbar.IsVisible || vbar.Visibility == Visibility.Visible);
            viewer.ScrollToHorizontalOffset(120);
            viewer.ScrollToVerticalOffset(140);
            Layout(host, 240, 160);
            Assert.Equal(horizontal ? 120 : 0, viewer.HorizontalOffset);
            Assert.Equal(vertical ? 140 : 0, viewer.VerticalOffset);
            Assert.Equal(viewer.HorizontalOffset, hbar.Value);
            Assert.Equal(viewer.VerticalOffset, vbar.Value);
            if (horizontal && vertical)
            {
                var image = new RenderTargetBitmap(240, 160, 96, 96, PixelFormats.Pbgra32);
                image.Render(host);
                var pixel = new byte[4];
                image.CopyPixels(new Int32Rect(238, 158, 1, 1), pixel, 4, 0);
                var background = Colors.MidnightBlue;
                Assert.Equal(new byte[] { background.B, background.G, background.R, 255 }, pixel);
            }
            Layout(host, 1100, 1100);
            Assert.Equal(Visibility.Collapsed, hbar.Visibility);
            Assert.Equal(Visibility.Collapsed, vbar.Visibility);
        });
    }

    [Fact]
    public void TextEditingStillScrollsAndKeepsItsSelection()
    {
        WpfTestHost.Invoke(() =>
        {
            var controls = LoadControls();
            Application.Current.Resources.MergedDictionaries.Add(controls);
            try
            {
                var text = new TextBox
                {
                    Text = string.Join("\r\n", Enumerable.Range(0, 80).Select(i => $"{i}: " + new string('字', 80))),
                    AcceptsReturn = true,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                };
                var host = new Border { Child = text };
                Layout(host, 240, 160);
                var scroll = Assert.IsType<ScrollViewer>(text.Template.FindName("PART_ContentHost", text));
                Assert.Same(controls[typeof(ScrollViewer)], scroll.Style);
                text.Select(4, 5);
                text.ScrollToEnd();
                Layout(host, 240, 160);
                Assert.True(scroll.VerticalOffset > 0);
                Assert.Equal(4, text.SelectionStart);
                Assert.Equal(5, text.SelectionLength);
            }
            finally { Application.Current.Resources.MergedDictionaries.Remove(controls); }
        });
    }

    private static ResourceDictionary LoadControls() => new()
    {
        Source = new Uri("/SnapCut;component/Themes/ThemedControls.xaml", UriKind.Relative),
    };

    private static void Layout(FrameworkElement root, double width, double height)
    {
        root.Measure(new Size(width, height));
        root.Arrange(new Rect(0, 0, width, height));
        root.UpdateLayout();
        root.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    }
}
