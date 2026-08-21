using System.Windows.Controls;
using Screenshot.App.Presentation;

namespace Screenshot.App.Tests;

public sealed class ToolbarDragInteractionTests
{
    [Fact]
    public void DragHintUsesTheRequestedIdleAndVisibleDurations()
    {
        Assert.Equal(
            "长按拖拽，双击自动吸附",
            ToolbarDragHintBehavior.HintText);
        Assert.Equal(
            TimeSpan.FromMilliseconds(650),
            ToolbarDragHintBehavior.IdleDelay);
        Assert.Equal(
            TimeSpan.FromSeconds(2),
            ToolbarDragHintBehavior.DisplayDuration);
    }

    [Fact]
    public void OnlyNonInteractiveToolbarSurfacesStartDragging()
    {
        WpfTestHost.Invoke(() =>
        {
            var root = new Border();
            var panel = new StackPanel();
            var blank = new Border();
            var button = new Button();
            var slider = new Slider();
            var dragHandle = new System.Windows.Controls.Primitives.Thumb();
            root.Child = panel;
            panel.Children.Add(blank);
            panel.Children.Add(button);
            panel.Children.Add(slider);
            panel.Children.Add(dragHandle);

            Assert.True(ToolbarDragInteraction.IsBlankSurface(blank, root));
            Assert.False(ToolbarDragInteraction.IsBlankSurface(button, root));
            Assert.False(ToolbarDragInteraction.IsBlankSurface(slider, root));
            Assert.False(ToolbarDragInteraction.IsBlankSurface(
                dragHandle,
                root));
            Assert.False(ToolbarDragInteraction.IsBlankSurface(
                new Border(),
                root));
        });
    }
}
