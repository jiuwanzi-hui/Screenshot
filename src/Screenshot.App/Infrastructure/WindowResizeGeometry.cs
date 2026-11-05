using System.Windows;

namespace Screenshot.App.Infrastructure;

internal static class WindowResizeGeometry
{
    // Always measure from the gesture origin. Accumulating clamped deltas makes
    // the edge drift away from the pointer after hitting a minimum size.
    internal static Rect Calculate(Rect start, string edge, Vector delta,
        double minWidth, double minHeight, double maxWidth, double maxHeight)
    {
        var left = edge.Contains("Left", StringComparison.Ordinal);
        var right = edge.Contains("Right", StringComparison.Ordinal);
        var top = edge.Contains("Top", StringComparison.Ordinal);
        var bottom = edge.Contains("Bottom", StringComparison.Ordinal);
        var width = Math.Clamp(start.Width + (left ? -delta.X : right ? delta.X : 0), minWidth, maxWidth);
        var height = Math.Clamp(start.Height + (top ? -delta.Y : bottom ? delta.Y : 0), minHeight, maxHeight);
        return new Rect(left ? start.Right - width : start.Left,
            top ? start.Bottom - height : start.Top, width, height);
    }
}
