using System.Drawing;

namespace Screenshot.App.Capture;

internal static class ScrollCaptureBorderGeometry
{
    internal static Rectangle GetBounds(Rectangle selection, Rectangle surface)
    {
        if (selection.Width <= 0 || selection.Height <= 0) return Rectangle.Empty;
        selection.Inflate(4, 4);
        // At a screen edge the border must move inward instead of leaving
        // the display. The sampler restores the content beneath these strips.
        return Rectangle.Intersect(selection, surface);
    }

    internal static Rectangle GetInputHole(Rectangle selection)
    {
        // Keep a narrow fallback outline inside the WPF window's native region
        // even when the selected content reaches the desktop boundary.
        selection.Inflate(-Math.Min(3, Math.Max(0, (selection.Width - 1) / 2)),
            -Math.Min(3, Math.Max(0, (selection.Height - 1) / 2)));
        return selection;
    }
}
