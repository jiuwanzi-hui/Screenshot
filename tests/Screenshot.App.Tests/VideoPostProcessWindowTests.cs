using System.Windows.Controls;
using System.Windows;
using Screenshot.App.Capture;
using WpfImage = System.Windows.Controls.Image;

namespace Screenshot.App.Tests;

[Collection(GlobalInputTestGroup.Name)]
public sealed class VideoPostProcessWindowTests
{
    [Fact]
    public void EditorUsesScrollableThemeSurfaceAndScrubbablePreview()
    {
        WpfTestHost.Invoke(() =>
        {
            var window = new VideoPostProcessWindow(@"C:\missing-preview.mp4");
            try
            {
                var root = Assert.IsType<Grid>(window.Content);
                var scrollViewer = Assert.Single(
                    root.Children.OfType<ScrollViewer>());
                Assert.Equal(
                    ScrollBarVisibility.Auto,
                    scrollViewer.VerticalScrollBarVisibility);
                Assert.Equal(
                    ScrollBarVisibility.Disabled,
                    scrollViewer.HorizontalScrollBarVisibility);

                var controls = Assert.IsType<StackPanel>(scrollViewer.Content);
                var previewPanel = Assert.IsType<StackPanel>(controls.Children[0]);
                var previewFrame = Assert.IsType<Border>(previewPanel.Children[0]);
                var previewLayers = Assert.IsType<Grid>(previewFrame.Child);
                var preview = Assert.Single(
                    previewLayers.Children.OfType<MediaElement>());
                var extractedFrame = Assert.Single(
                    previewLayers.Children.OfType<WpfImage>());
                Assert.True(preview.ScrubbingEnabled);
                Assert.Equal(MediaState.Manual, preview.LoadedBehavior);
                Assert.Equal(Visibility.Visible, extractedFrame.Visibility);

                var timelinePanel = Assert.IsType<StackPanel>(
                    controls.Children[1]);
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
                Assert.Empty(controls.Children.OfType<Grid>());
            }
            finally
            {
                window.Close();
            }
        });
    }
}
