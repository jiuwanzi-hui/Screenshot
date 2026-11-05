using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Screenshot.App.Presentation;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace Screenshot.App.Editor;

internal static class TextAnnotationOptions
{
    internal static void Configure(ToggleButton button, Func<ImageEditorCanvas?> getCanvas,
        Action? selectTool = null, RoutedEventHandler? closed = null, bool inlineOptions = false)
    {
        button.Width = 42;
        button.Padding = new Thickness(0);
        var menu = new ContextMenu { PlacementTarget = button, Placement = PlacementMode.Bottom };
        menu.SetResourceReference(FrameworkElement.StyleProperty, "ThemedContextMenuStyle");
        foreach (var enabled in new[] { false, true })
        {
            var item = new MenuItem { Header = enabled ? "有背景" : "无背景", Tag = enabled, IsCheckable = true };
            item.SetResourceReference(FrameworkElement.StyleProperty, "ThemedMenuItemStyle");
            item.Click += (_, _) =>
            {
                if (getCanvas() is { } canvas)
                {
                    canvas.SetTextBackground(enabled);
                    if (!canvas.HasSelectedTextAnnotation) (selectTool ?? (() => button.IsChecked = true))();
                }
                else AnnotationToolPreferences.SetTextBackground(enabled);
                if (inlineOptions) UpdateInlineIcon(button, enabled);
            };
            menu.Items.Add(item);
        }
        menu.Opened += (_, _) =>
        {
            var canvas = getCanvas();
            canvas?.PrepareTextBackgroundOptions();
            var current = canvas?.TextBackgroundEnabled ?? AnnotationToolPreferences.TextBackground;
            foreach (MenuItem item in menu.Items)
            {
                item.Header = UiLocalization.Get((bool)item.Tag ? "有背景" : "无背景");
                item.IsChecked = (bool)item.Tag == current;
            }
            if (inlineOptions) UpdateInlineIcon(button, current);
        };
        if (closed is not null) menu.Closed += closed;
        button.ContextMenu = menu;
        var content = CreateIcon();
        if (inlineOptions)
        {
            UpdateInlineIcon(button, getCanvas()?.TextBackgroundEnabled ?? AnnotationToolPreferences.TextBackground);
            return;
        }
        var arrow = new ToolbarDropDownButton { ToolTip = "文字背景", FontSize = 10 };
        arrow.Click += (_, e) =>
        {
            getCanvas()?.PrepareTextBackgroundOptions();
            menu.IsOpen = !menu.IsOpen;
            e.Handled = true;
        };
        content.Children.RemoveAt(content.Children.Count - 1); // replace preview chevron with the interactive arrow
        content.Children.Add(arrow);
        button.Content = content;
    }

    private static void UpdateInlineIcon(ToggleButton button, bool background)
    {
        if (button.Content is Grid { Tag: bool previous } && previous == background) return;
        var content = CreateIcon();
        content.Children.RemoveAt(content.Children.Count - 1);
        content.Width = 20;
        content.Tag = background;
        ((FrameworkElement)content.Children[0]).HorizontalAlignment = HorizontalAlignment.Center;
        if (background)
        {
            var outline = new System.Windows.Shapes.Rectangle
            {
                Width = 20, Height = 24, RadiusX = 2, RadiusY = 2,
                StrokeThickness = 1, Opacity = 0.7, IsHitTestVisible = false,
            };
            outline.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "EditorToolbarIconBrush");
            content.Children.Insert(0, outline);
        }
        button.Content = content;
    }

    // Settings layout and preview use the same split icon as every editor host.
    internal static Grid CreateIcon()
    {
        var content = new Grid { Width = 30, Height = 28 };
        var path = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M 2,2 H 16 M 9,2 V 18"), Width = 18, Height = 20,
            Stretch = Stretch.Uniform, StrokeThickness = 1.7, HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
        };
        path.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "EditorToolbarIconBrush");
        content.Children.Add(path);
        var arrow = new TextBlock
        {
            Text = "▾", FontSize = 10, HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        arrow.SetResourceReference(TextBlock.ForegroundProperty, "EditorToolbarIconBrush");
        content.Children.Add(arrow);
        return content;
    }
}
