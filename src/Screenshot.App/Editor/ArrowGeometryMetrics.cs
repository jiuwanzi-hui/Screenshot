namespace Screenshot.App.Editor;

/// <summary>
/// Shared proportions for arrows rendered by the editor and native previews.
/// Keeping these values in one place prevents a thick stroke from leaving a
/// visually tiny head or a short arrow from producing an inverted polygon.
/// </summary>
internal readonly record struct ArrowGeometryMetrics(
    double HeadLength,
    double HeadHalfWidth,
    double BaseHalfWidth,
    double TailHalfWidth)
{
    internal static ArrowGeometryMetrics For(double length, double strokeWidth)
    {
        length = double.IsFinite(length) ? Math.Max(0, length) : 0;
        strokeWidth = double.IsFinite(strokeWidth)
            ? Math.Max(1, strokeWidth)
            : 1;

        if (length <= 0)
        {
            return new ArrowGeometryMetrics(0, 0, strokeWidth * 1.1, strokeWidth * 0.55);
        }

        // Adapt the head fraction to the stroke-to-length ratio. A short,
        // thick arrow needs more room for its head; a long, thin arrow should
        // keep the head compact instead of looking top-heavy.
        var strokeRatio = strokeWidth / length;
        var ratioProgress = Math.Min(
            1,
            Math.Max(0, (strokeRatio - 0.01) / 0.14));
        var headFraction = 0.08 + (0.30 * ratioProgress);
        var headLength = Math.Min(
            Math.Max(length * headFraction, strokeWidth * 3.2),
            length * 0.48);
        var headHalfWidth = Math.Min(
            length * 0.40,
            Math.Max(strokeWidth * 1.8, headLength * 0.28));
        // Let the shaft widen toward the head, but cap that widening for short
        // thick arrows so the body does not swallow the arrowhead.
        var tailHalfWidth = Math.Max(0.5, strokeWidth * 0.55);
        var baseHalfWidth = Math.Min(
            Math.Max(1.4, Math.Max(strokeWidth * 1.12, headHalfWidth * 0.24)),
            Math.Max(strokeWidth * 0.8, headLength * 0.24));
        return new ArrowGeometryMetrics(
            headLength,
            headHalfWidth,
            baseHalfWidth,
            tailHalfWidth);
    }
}
