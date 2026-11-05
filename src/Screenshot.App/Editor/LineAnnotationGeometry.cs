using System.Windows;
using System.Windows.Media;
using Point = System.Windows.Point;
using System.Runtime.CompilerServices;

namespace Screenshot.App.Editor;

internal static class LineAnnotationGeometry
{
    private static readonly ConditionalWeakTable<LineAnnotation, StreamGeometry> Cache = new();

    public static StreamGeometry Get(LineAnnotation annotation) => Cache.GetValue(annotation,
        static line => Create(line.Start, line.End, line.StrokeWidth, line.IsWavy));

    // Analytic curves keep the cost independent of mouse polling rate. The
    // endpoints always lie on the baseline, including short and reversed lines.
    public static StreamGeometry Create(Point start, Point end, double width, bool wavy)
    {
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(start, isFilled: false, isClosed: false);
            var direction = end - start;
            var length = direction.Length;
            if (!wavy || length < 2)
            {
                context.LineTo(end, isStroked: true, isSmoothJoin: false);
            }
            else
            {
                direction /= length;
                var normal = new Vector(-direction.Y, direction.X);
                var waves = Math.Clamp((int)Math.Round(length / Math.Max(16, width * 6)), 1, 512);
                var step = length / (waves * 2);
                var amplitude = Math.Min(Math.Max(3, width * 1.5), step / 2);
                for (var i = 0; i < waves * 2; i++)
                {
                    var origin = start + direction * (i * step);
                    var crest = normal * (amplitude * 4 / 3 * (i % 2 == 0 ? 1 : -1));
                    context.BezierTo(origin + direction * (step / 3) + crest,
                        origin + direction * (step * 2 / 3) + crest,
                        start + direction * ((i + 1) * step), true, true);
                }
            }
        }
        geometry.Freeze();
        return geometry;
    }
}
