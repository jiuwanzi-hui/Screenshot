using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Screenshot.App.Presentation;
using Path = System.Windows.Shapes.Path;
using Control = System.Windows.Controls.Control;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;
using Orientation = System.Windows.Controls.Orientation;
using Cursors = System.Windows.Input.Cursors;
using Brushes = System.Windows.Media.Brushes;

namespace Screenshot.App.Editor;

/// <summary>Shared split-button presentation for screenshot, pin and recording tools.</summary>
internal static class AnnotationVariantButton
{
    public static void Configure(
        ToggleButton button, EditorTool group, Action<EditorTool> select, bool showLabel = false, bool inlineOptions = false)
    {
        ToolbarOptionsRow.SetUsesInlineOptions(button, inlineOptions);
        var variants = group == EditorTool.DoubleArrow
            ? new[] { EditorTool.DoubleArrow, EditorTool.HollowDoubleArrow }
            : group == EditorTool.Mosaic
            ? new[] { EditorTool.Mosaic, EditorTool.RectangleMosaic }
            : new[] { EditorTool.Line, EditorTool.WavyLine };
        var menu = new ContextMenu { PlacementTarget = button, Placement = PlacementMode.Bottom };
        menu.SetResourceReference(FrameworkElement.StyleProperty, "ThemedContextMenuStyle");
        foreach (var variant in variants)
        {
            var item = new MenuItem { Header = Label(variant), Tag = variant, IsCheckable = true, Icon = CreateIcon(variant) };
            if (group == EditorTool.DoubleArrow)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal };
                var menuIcon = CreateIcon(variant);
                menuIcon.Margin = new Thickness(0, 0, 8, 0);
                row.Children.Add(menuIcon);
                row.Children.Add(new TextBlock { Text = Label(variant), VerticalAlignment = VerticalAlignment.Center });
                item.Header = row;
                item.Icon = null;
            }
            item.SetResourceReference(FrameworkElement.StyleProperty, "ThemedMenuItemStyle");
            item.Click += (_, _) =>
            {
                AnnotationToolPreferences.SetVariant(group, variant);
                Update(button, variant, showLabel);
                select(variant);
            };
            menu.Items.Add(item);
        }
        button.ContextMenu = menu;
        menu.Opened += (_, _) =>
        {
            foreach (MenuItem item in menu.Items)
                item.IsChecked = item.Tag?.ToString() == button.Tag?.ToString();
        };
        Update(button, AnnotationToolPreferences.GetVariant(group), showLabel);
    }

    public static void Update(ToggleButton button, EditorTool variant, bool showLabel = false)
    {
        button.Tag = variant.ToString();
        if (button.ContextMenu is { } menu)
            foreach (MenuItem item in menu.Items)
                item.IsChecked = item.Tag is EditorTool tool && tool == variant;
        var inline = ToolbarOptionsRow.GetUsesInlineOptions(button);
        button.ToolTip = inline ? Label(variant) : $"{Label(variant)}（下拉切换样式）";
        if (inline)
        {
            button.Content = CreateIcon(variant);
            return;
        }
        if (variant is EditorTool.DoubleArrow or EditorTool.HollowDoubleArrow)
        {
            UpdateDoubleArrowContent(button, variant);
            return;
        }
        var content = new Grid { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = System.Windows.HorizontalAlignment.Center };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var icon = CreateIcon(variant);
        icon.SetBinding(System.Windows.Shapes.Shape.StrokeProperty,
            new System.Windows.Data.Binding(nameof(Control.Foreground)) { Source = button });
        var main = new StackPanel { Orientation = Orientation.Horizontal };
        main.Children.Add(icon);
        if (showLabel)
            main.Children.Add(new TextBlock
            {
                Text = Label(variant), Margin = new Thickness(5, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            });
        content.Children.Add(main);
        var arrow = new ToolbarDropDownButton
        {
            FontSize = 10,
            VerticalAlignment = VerticalAlignment.Center, Cursor = Cursors.Hand,
            Background = Brushes.Transparent,
        };
        Grid.SetColumn(arrow, 1);
        arrow.Click += (_, e) =>
        {
            if (button.ContextMenu is { } menu)
                menu.IsOpen = true;
            e.Handled = true;
        };
        content.Children.Add(arrow);
        button.Content = content;
    }

    private static void UpdateDoubleArrowContent(ToggleButton button, EditorTool variant)
    {
        var content = new Grid { Width = 29 };
        var icon = CreateIcon(variant);
        icon.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
        icon.SetBinding(variant == EditorTool.DoubleArrow
                ? System.Windows.Shapes.Shape.FillProperty : System.Windows.Shapes.Shape.StrokeProperty,
            new System.Windows.Data.Binding(nameof(Control.Foreground)) { Source = button });
        content.Children.Add(icon);
        var arrow = new ToolbarDropDownButton
        {
            FontSize = 9, Cursor = Cursors.Hand,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Background = Brushes.Transparent, ToolTip = "选择箭头样式",
        };
        arrow.Click += (_, e) =>
        {
            if (button.ContextMenu is { } menu) menu.IsOpen = true;
            e.Handled = true;
        };
        content.Children.Add(arrow);
        button.Content = content;
    }

    private static Path CreateIcon(EditorTool variant)
    {
        var icon = new Path
        {
            Width = 18, Height = 18, Stretch = Stretch.Uniform,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            Data = Geometry.Parse(variant switch
            {
                EditorTool.DoubleArrow or EditorTool.HollowDoubleArrow => DoubleArrowGeometry.Icon,
                EditorTool.Line => "M 2,16 L 16,2",
                EditorTool.WavyLine => "M 1,10 C 4,0 6,0 9,10 S 14,20 17,8",
                EditorTool.RectangleMosaic => "M 1,6 V 1 H 6 M 12,1 H 17 V 6 M 17,12 V 17 H 12 M 6,17 H 1 V 12 M 6,6 H 12 V 12 H 6 Z",
                _ => AnnotationToolIcons.Mosaic,
            }),
            StrokeThickness = 1.5, StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
        };
        icon.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "AppControlForegroundBrush");
        if (variant == EditorTool.DoubleArrow)
        {
            icon.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "AppControlForegroundBrush");
            icon.Stroke = Brushes.Transparent;
            icon.StrokeThickness = 0;
        }
        else if (variant == EditorTool.HollowDoubleArrow)
        {
            icon.Fill = Brushes.Transparent;
            // Two arrows share an 18 px icon; preserve a visible hollow shaft.
            icon.StrokeThickness = 1.2;
        }
        return icon;
    }

    private static string Label(EditorTool tool) => tool switch
    {
        EditorTool.DoubleArrow => "双向实心箭头",
        EditorTool.HollowDoubleArrow => "双向空心箭头",
        EditorTool.Line => "直线",
        EditorTool.WavyLine => "波浪线",
        EditorTool.RectangleMosaic => "矩形马赛克",
        _ => "涂抹马赛克",
    };
}
