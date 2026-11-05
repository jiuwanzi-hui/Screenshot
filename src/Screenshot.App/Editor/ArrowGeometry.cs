using System.Windows;
using System.Windows.Media;
using Point = System.Windows.Point;

namespace Screenshot.App.Editor;

internal static class ArrowGeometry
{
    internal static PointCollection CreateTaperedPoints(
        Point start,
        Point end,
        double strokeWidth)
    {
        var direction = end - start;
        var length = direction.Length;

        if (length < 1)
        {
            return [start, end];
        }

        direction.Normalize();
        var perpendicular = new Vector(-direction.Y, direction.X);
        // Keep the head and shaft proportional for both long drags and thick
        // strokes. The shared metrics also handle tiny drags without an
        // inverted clamp range during the first mouse move.
        var metrics = ArrowGeometryMetrics.For(length, strokeWidth);
        var headLength = metrics.HeadLength;
        var headHalfWidth = metrics.HeadHalfWidth;
        var baseHalfWidth = metrics.BaseHalfWidth;
        var tailHalfWidth = metrics.TailHalfWidth;
        var tailTransition = Math.Min(
            Math.Max(strokeWidth * 1.5, headLength * 0.16),
            length * 0.12);
        var tailBase = start + (direction * tailTransition);
        var basePoint = end - (direction * headLength);
        var outerHeadBase = basePoint - (direction * metrics.NeckInset);

        return
        [
            start,
            tailBase + (perpendicular * tailHalfWidth),
            basePoint + (perpendicular * baseHalfWidth),
            outerHeadBase + (perpendicular * headHalfWidth),
            end,
            outerHeadBase - (perpendicular * headHalfWidth),
            basePoint - (perpendicular * baseHalfWidth),
            tailBase - (perpendicular * tailHalfWidth),
        ];
    }
}
