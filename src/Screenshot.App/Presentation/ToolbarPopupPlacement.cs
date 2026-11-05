using System.Windows;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace Screenshot.App.Presentation;

internal static class ToolbarPopupPlacement
{
    // All arguments use the caller's coordinate system (screen pixels or DIPs).
    internal static Point Below(Rect anchor, Size size, Rect bounds, double gap = 8)
    {
        var below = anchor.Bottom + gap;
        var above = anchor.Top - gap - size.Height;
        var y = below + size.Height <= bounds.Bottom ? below
            : above >= bounds.Top ? above
            : bounds.Bottom - anchor.Bottom >= anchor.Top - bounds.Top ? below : above;
        return new Point(
            Math.Clamp(anchor.Left, bounds.Left, Math.Max(bounds.Left, bounds.Right - size.Width)),
            Math.Clamp(y, bounds.Top, Math.Max(bounds.Top, bounds.Bottom - size.Height)));
    }
}
