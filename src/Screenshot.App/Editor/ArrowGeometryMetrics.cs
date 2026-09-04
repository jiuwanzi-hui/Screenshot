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
    double TailHalfWidth,
    double NeckInset)
{
    internal static ArrowGeometryMetrics For(double length, double strokeWidth)
    {
        length = double.IsFinite(length) ? Math.Max(0, length) : 0;
        strokeWidth = double.IsFinite(strokeWidth)
            ? Math.Max(1, strokeWidth)
            : 1;

        if (length <= 0)
        {
            return new ArrowGeometryMetrics(
                0,
                0,
                strokeWidth * 1.1,
                strokeWidth * 0.55,
                0);
        }

        // Adapt the head fraction to the stroke-to-length ratio. A short,
        // thick arrow needs more room for its head; a long, thin arrow should
        // keep the head compact instead of looking top-heavy.
        var strokeRatio = strokeWidth / length;
        var ratioProgress = Math.Min(
            1,
            Math.Max(0, (strokeRatio - 0.01) / 0.14));
        var headFraction = 0.03 + (0.08 * ratioProgress);
        var preferredHeadLength =
            (length * headFraction) + 9 + (strokeWidth * 2.8);
        var headLength = Math.Min(
            Math.Max(preferredHeadLength, strokeWidth * 4.2),
            Math.Min(length * 0.55, 42 + (strokeWidth * 1.5)));
        var headWidthFraction = 0.28 + (0.08 * ratioProgress);
        var headHalfWidth = Math.Min(
            length * 0.40,
            // Keep the shoulders visibly wider than the shaft even when a
            // long, thin arrow makes the stroke-width floor the limiting
            // value. The resulting shoulder-to-base step gives the head its
            // slight downward barb instead of a narrow symmetric triangle.
            Math.Max(strokeWidth * 2.2, headLength * headWidthFraction));
        // Let the shaft widen toward the head, but cap that widening for short
        // thick arrows so the body does not swallow the arrowhead.
        var tailHalfWidth = Math.Max(0.5, strokeWidth * 0.55);
        var baseHalfWidth = Math.Min(
            Math.Max(1.2, Math.Max(strokeWidth * 0.90, headHalfWidth * 0.18)),
            Math.Max(strokeWidth * 0.72, headLength * 0.20));
        // Pull the outer shoulders back toward the tail so the neck edges
        // lean away from the tip instead of meeting it as a blunt crossbar.
        var neckInset = Math.Min(length * 0.10, headLength * 0.16);
        return new ArrowGeometryMetrics(
            headLength,
            headHalfWidth,
            baseHalfWidth,
            tailHalfWidth,
            neckInset);
    }
}
