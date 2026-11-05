using System.Windows;
using System.Windows.Media;
using DrawingColor = System.Drawing.Color;
using Brush = System.Windows.Media.Brush;

namespace Screenshot.App.Capture;

// A dynamic resource also updates native strip windows, including frames that
// were prepared before the user changed themes. No polling or layout is needed.
internal static class SelectionFrameTheme
{
    private static readonly DependencyProperty FrameProperty = DependencyProperty.RegisterAttached(
        "Frame", typeof(NativeSelectionFrameWindow), typeof(SelectionFrameTheme));
    private static readonly DependencyProperty BrushProperty = DependencyProperty.RegisterAttached(
        "Brush", typeof(Brush), typeof(SelectionFrameTheme),
        new PropertyMetadata(null, (owner, _) => Refresh((FrameworkElement)owner)));

    internal static void Attach(FrameworkElement owner, NativeSelectionFrameWindow frame)
    {
        owner.SetValue(FrameProperty, frame);
        owner.SetResourceReference(BrushProperty, "SelectionBorderBrush");
        Refresh(owner);
    }

    internal static void Refresh(FrameworkElement owner)
    {
        if (owner.GetValue(FrameProperty) is not NativeSelectionFrameWindow frame) return;
        switch (owner.GetValue(BrushProperty))
        {
            case SolidColorBrush solid:
                frame.SetBorderColor(Opaque(solid.Color));
                break;
            case GradientBrush { GradientStops.Count: > 0 } gradient:
                frame.SetBorderColors(Opaque(gradient.GradientStops[0].Color), Opaque(gradient.GradientStops[^1].Color));
                break;
        }
    }

    private static DrawingColor Opaque(System.Windows.Media.Color color) => DrawingColor.FromArgb(color.R, color.G, color.B);
}
