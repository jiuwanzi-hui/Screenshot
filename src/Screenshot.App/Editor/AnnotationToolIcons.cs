using System.Windows;
using System.Windows.Media;
using Path = System.Windows.Shapes.Path;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace Screenshot.App.Editor;

internal static class AnnotationToolIcons
{
    internal const string Number = "M 2,3 L 4,1 V 7 M 2,7 H 6 M 2,12 C 2,9 6,9 6,12 L 2,17 H 6 M 10,4 H 18 M 10,13 H 18";
    internal const string Mosaic = "M 1,7 H 5 V 11 H 1 Z M 7,1 H 11 V 5 H 7 Z M 7,7 H 11 V 11 H 7 Z M 13,7 H 17 V 11 H 13 Z M 7,13 H 11 V 17 H 7 Z M 13,13 H 17 V 17 H 13 Z";

    internal static Path Create(string geometry)
    {
        var icon = new Path
        {
            Data = Geometry.Parse(geometry), Width = 18, Height = 18, Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            StrokeThickness = 1.5, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
        };
        icon.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "EditorToolbarIconBrush");
        return icon;
    }
}
