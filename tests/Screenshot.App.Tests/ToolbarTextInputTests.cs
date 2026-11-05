using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Screenshot.App.Capture;
using Screenshot.App.Core;
using Screenshot.App.Infrastructure;
using Screenshot.App.Presentation;

namespace Screenshot.App.Tests;

public sealed class ToolbarTextInputTests
{
    private static readonly string[] ButtonNames =
    [
        "OcrButton", "CopyRecognizedTextButton", "TranslateButton",
        "PrivacyRedactionButton", "WatermarkButton",
    ];

    [Theory]
    [InlineData(AppTheme.NeonDeep, 0.5)]
    [InlineData(AppTheme.NeonDeep, 1.0)]
    [InlineData(AppTheme.GlassInk, 1.5)]
    public void TextGlyphMouseDownReachesActionButtonWithoutStartingSelection(AppTheme theme, double scale)
    {
        WpfTestHost.Invoke(() =>
        {
            using var overlay = new CaptureOverlayWindow(null, isStartupPrewarm: true, deferInitialScreenCapture: true);
            try
            {
                var root = Assert.IsAssignableFrom<FrameworkElement>(overlay.Content);
                overlay.Content = null;
                root.Resources = overlay.Resources;
                AppThemeManager.ApplySettingsPalette(root.Resources, theme);
                var toolbar = (Border)overlay.FindName("CaptureToolbar");
                toolbar.Visibility = Visibility.Visible;
                toolbar.LayoutTransform = new ScaleTransform(scale, scale);
                foreach (var name in ButtonNames)
                    ((Button)overlay.FindName(name)).Visibility = Visibility.Visible;
                using var source = new HwndSource(new HwndSourceParameters("Offscreen toolbar text input")
                { WindowStyle = unchecked((int)0x80000000), Width = 1600, Height = 900 });
                source.RootVisual = root;
                root.Measure(new Size(1600, 900));
                root.Arrange(new Rect(0, 0, 1600, 900));
                root.UpdateLayout();

                foreach (var name in ButtonNames)
                {
                    var button = Assert.IsType<Button>(overlay.FindName(name));
                    var label = Descendants(button).OfType<TextBlock>().FirstOrDefault();
                    Assert.True(label is not null, name + " did not create its text glyph");
                    var run = Assert.IsType<Run>(label.Inlines.FirstInline);
                    var reachedButton = false;
                    MouseButtonEventHandler handler = (_, e) =>
                    {
                        Assert.Same(run, e.OriginalSource);
                        Assert.False(e.Handled);
                        reachedButton = true;
                    };
                    button.PreviewMouseLeftButtonDown += handler;
                    try
                    {
                        // Route the actual text-content source through the capture
                        // surface, rather than bypassing input with Button.Click.
                        run.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                        { RoutedEvent = Mouse.PreviewMouseDownEvent });
                        Assert.True(reachedButton, name + " did not receive the glyph click");
                        Assert.False(ToolbarDragInteraction.IsBlankSurface(run, toolbar));
                    }
                    finally { button.PreviewMouseLeftButtonDown -= handler; }
                }
            }
            finally { overlay.Close(); }
        });
    }

    [Fact]
    public void NestedTextContentIsMatchedOnlyToItsOwnSurface()
    {
        WpfTestHost.Invoke(() =>
        {
            var run = new Run("文");
            var text = new TextBlock(new Span(run));
            var root = new Border { Child = text };

            Assert.True(InputElementTree.IsWithin(run, root));
            Assert.True(InputElementTree.IsWithin(run, text));
            Assert.True(InputElementTree.IsWithin(root, root));
            Assert.False(InputElementTree.IsWithin(run, new Border()));
            Assert.False(InputElementTree.IsWithin(new Run("译"), root));
            Assert.False(InputElementTree.IsWithin(null, root));
        });
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject value)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(value); i++)
        {
            var child = VisualTreeHelper.GetChild(value, i);
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
}
