using System.Windows.Controls;
using System.Windows;
using Screenshot.App.Capture;
using WpfImage = System.Windows.Controls.Image;

namespace Screenshot.App.Tests;

[Collection(GlobalInputTestGroup.Name)]
public sealed class VideoPostProcessWindowTests
{
    [Fact]
    public void EditorKeepsTimelineAndActionsVisibleBelowTheResizablePreview()
    {
        WpfTestHost.Invoke(() =>
        {
            var window = new VideoPostProcessWindow(@"C:\missing-preview.mp4");
            try
            {
                var shell = Assert.IsType<Border>(window.Content);
                var chrome = Assert.IsType<Grid>(shell.Child);
                var root = Assert.IsType<Grid>(chrome.Children[1]);
                Assert.Empty(root.Children.OfType<ScrollViewer>());
                var previewCard = Assert.IsType<Border>(root.Children[1]);
                var previewPanel = Assert.IsType<Grid>(previewCard.Child);
                var previewFrame = Assert.IsType<Border>(previewPanel.Children[0]);
                Assert.True(double.IsNaN(previewFrame.Height));
                var previewLayers = Assert.IsType<Grid>(previewFrame.Child);
                var preview = Assert.Single(
                    previewLayers.Children.OfType<MediaElement>());
                var extractedFrame = Assert.Single(
                    previewLayers.Children.OfType<WpfImage>());
                Assert.True(preview.ScrubbingEnabled);
                Assert.Equal(MediaState.Manual, preview.LoadedBehavior);
                Assert.Equal(Visibility.Visible, extractedFrame.Visibility);

                var timelineCard = Assert.IsType<Border>(root.Children[2]);
                var timelinePanel = Assert.IsType<StackPanel>(timelineCard.Child);
                var timelineBorder = Assert.Single(
                    timelinePanel.Children.OfType<Border>());
                var timelineScroll = Assert.IsType<ScrollViewer>(
                    timelineBorder.Child);
                Assert.Equal(
                    ScrollBarVisibility.Disabled,
                    timelineScroll.HorizontalScrollBarVisibility);
                Assert.Equal(
                    ScrollBarVisibility.Disabled,
                    timelineScroll.VerticalScrollBarVisibility);
                var timelineLayers = Assert.IsType<Grid>(
                    timelineScroll.Content);
                Assert.Equal(2, timelineLayers.Children.OfType<Canvas>().Count());
                var timelineOverlay = timelineLayers.Children
                    .OfType<Canvas>()
                    .Last();
                var handles = timelineOverlay.Children
                    .OfType<System.Windows.Controls.Primitives.Thumb>()
                    .ToArray();
                Assert.Equal(2, handles.Length);
                Assert.All(handles, handle =>
                {
                    Assert.NotEqual(
                        DependencyProperty.UnsetValue,
                        handle.ReadLocalValue(FrameworkElement.StyleProperty));
                });
                var actions = Assert.Single(root.Children.OfType<System.Windows.Controls.Primitives.UniformGrid>());
                Assert.Equal(4, actions.Children.Count);
                shell.Measure(new Size(720, 700));
                shell.Arrange(new Rect(0, 0, 720, 700));
                shell.UpdateLayout();
                var previewHeight = previewFrame.ActualHeight;
                var origin = actions.TranslatePoint(new Point(), shell);
                Assert.True(origin.Y + actions.ActualHeight <= shell.ActualHeight + 1);
                Assert.True(previewHeight >= 72);
                shell.Measure(new Size(1000, 1000));
                shell.Arrange(new Rect(0, 0, 1000, 1000));
                shell.UpdateLayout();
                Assert.True(previewFrame.ActualHeight > previewHeight + 200);
            }
            finally
            {
                window.Close();
            }
        });
    }
}
