using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using Point = System.Windows.Point;

namespace Screenshot.App.Editor;

internal static class DoubleArrowGeometry
{
    internal const string Icon = "M 1,4 H 11 V 0 L 18,7 H 1 Z M 17,13 H 7 V 17 L 0,10 H 17 Z";
    internal const string OutlineIcon = "M 1,7 H 17 L 11,1 M 17,10 H 1 L 7,16";
    private static readonly ConditionalWeakTable<ArrowAnnotation, StreamGeometry> Cache = new();

    internal static StreamGeometry Get(ArrowAnnotation arrow) => Cache.GetValue(arrow,
        static annotation => Create(annotation.Start, annotation.End, annotation.StrokeWidth));

    internal static StreamGeometry Create(Point start, Point end, double strokeWidth)
    {
        var geometry = new StreamGeometry();
        var direction = end - start;
        if (direction.Length >= 1)
        {
            var metrics = ArrowGeometryMetrics.For(direction.Length, strokeWidth);
            direction.Normalize();
            // Only the shaft faces the gap. Size the spacing from its width,
            // with room for hollow outlines, instead of the full arrowhead.
            var shaftHalfWidth = Math.Max(metrics.BaseHalfWidth, metrics.TailHalfWidth);
            var outlineHalfWidth = Math.Max(1.5, strokeWidth * .6) / 2;
            var offset = new Vector(-direction.Y, direction.X) *
                (shaftHalfWidth + outlineHalfWidth + Math.Max(1, strokeWidth * .35));
            using var context = geometry.Open();
            AddFigure(context, ArrowGeometry.CreateTaperedPoints(start - offset, end - offset, strokeWidth));
            AddFigure(context, ArrowGeometry.CreateTaperedPoints(end + offset, start + offset, strokeWidth));
        }
        geometry.Freeze();
        return geometry;
    }

    internal static bool IsHit(ArrowAnnotation arrow, Point point)
    {
        var geometry = Get(arrow);
        return geometry.FillContains(point) || geometry.StrokeContains(
            new System.Windows.Media.Pen(System.Windows.Media.Brushes.Black, Math.Max(6, arrow.StrokeWidth)), point);
    }

    private static void AddFigure(StreamGeometryContext context, PointCollection points)
    {
        // Omit the inner shoulder of each head, leaving the outward barb and
        // the original shaft. The reversed second arrow has its barb below.
        // Keep each outline closed independently for the hollow style.
        context.BeginFigure(points[0], isFilled: true, isClosed: true);
        for (var i = 1; i < points.Count; i++)
        {
            if (i == 3) continue; // The full single-arrow head's inner shoulder.
            context.LineTo(points[i], isStroked: true, isSmoothJoin: false);
        }
    }
}
