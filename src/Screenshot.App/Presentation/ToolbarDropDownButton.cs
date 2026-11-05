using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Button = System.Windows.Controls.Button;
using Brushes = System.Windows.Media.Brushes;
using Cursors = System.Windows.Input.Cursors;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace Screenshot.App.Presentation;

// A full-height hit surface, independent of the tiny triangle's glyph bounds.
// Button releases its own capture before Click opens the popup, so the opening
// mouse-up cannot also dismiss the menu or toggle the surrounding tool.
public sealed class ToolbarDropDownButton : Button
{
    public ToolbarDropDownButton()
    {
        Content = "▾";
        Width = 14;
        Height = 28;
        MinWidth = MinHeight = 0;
        Padding = BorderThickness = new Thickness(0);
        Focusable = false;
        ClickMode = ClickMode.Release;
        Background = Brushes.Transparent;
        Cursor = Cursors.Hand;
        FontSize = 9;
        HorizontalAlignment = HorizontalAlignment.Right;
        VerticalAlignment = VerticalAlignment.Center;
        SetResourceReference(ForegroundProperty, "EditorToolbarIconBrush");
        var surface = new FrameworkElementFactory(typeof(Border));
        surface.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        // The glyph is decoration. Keep input on the same button surface at
        // its centre and in the padding (including themed text adornments).
        content.SetValue(IsHitTestVisibleProperty, false);
        content.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        surface.AppendChild(content);
        Template = new ControlTemplate(typeof(Button)) { VisualTree = surface };
    }
}
