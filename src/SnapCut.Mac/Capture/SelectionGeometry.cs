using Avalonia;
using SnapCut.Mac.Native;

namespace SnapCut.Mac.Capture;

internal static class SelectionGeometry
{
    public static CGRect ToGlobalRect(
        Rect selection,
        Size overlaySize,
        CGRect displayBounds)
    {
        if (overlaySize.Width <= 0 || overlaySize.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(overlaySize),
                "框选窗口必须具有有效尺寸。");
        }

        var normalized = selection.Normalize();
        var scaleX = displayBounds.Size.Width / overlaySize.Width;
        var scaleY = displayBounds.Size.Height / overlaySize.Height;
        return new CGRect(
            displayBounds.Left + (normalized.X * scaleX),
            displayBounds.Top + (normalized.Y * scaleY),
            normalized.Width * scaleX,
            normalized.Height * scaleY);
    }
}
