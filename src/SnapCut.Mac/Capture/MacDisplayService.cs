using SnapCut.Mac.Native;

namespace SnapCut.Mac.Capture;

/// <summary>A connected display in global display-space coordinates (points).</summary>
internal sealed record MacDisplay(
    uint DisplayId,
    CGRect Bounds,
    int PixelWidth,
    int PixelHeight,
    bool IsMain)
{
    public double Scale => Bounds.Size.Width > 0
        ? PixelWidth / Bounds.Size.Width
        : 1;
}

internal static class MacDisplayService
{
    public static IReadOnlyList<MacDisplay> GetActiveDisplays()
    {
        const uint maximumDisplays = 16;
        var identifiers = new uint[maximumDisplays];

        if (CoreGraphics.CGGetActiveDisplayList(
                maximumDisplays,
                identifiers,
                out var count) != 0)
        {
            throw new InvalidOperationException("无法枚举 macOS 显示器。");
        }

        var mainDisplay = CoreGraphics.CGMainDisplayID();
        var displays = new List<MacDisplay>((int)count);

        for (var index = 0; index < count; index++)
        {
            var displayId = identifiers[index];
            displays.Add(new MacDisplay(
                displayId,
                CoreGraphics.CGDisplayBounds(displayId),
                (int)CoreGraphics.CGDisplayPixelsWide(displayId),
                (int)CoreGraphics.CGDisplayPixelsHigh(displayId),
                displayId == mainDisplay));
        }

        return displays;
    }

    /// <summary>
    /// The display whose bounds contain the center of <paramref name="rect"/>,
    /// falling back to the main display.
    /// </summary>
    public static MacDisplay SelectDisplayFor(CGRect rect)
    {
        var displays = GetActiveDisplays();
        var centerX = rect.Left + (rect.Size.Width / 2);
        var centerY = rect.Top + (rect.Size.Height / 2);

        foreach (var display in displays)
        {
            if (centerX >= display.Bounds.Left &&
                centerX < display.Bounds.Right &&
                centerY >= display.Bounds.Top &&
                centerY < display.Bounds.Bottom)
            {
                return display;
            }
        }

        return displays.FirstOrDefault(display => display.IsMain)
            ?? displays[0];
    }
}
