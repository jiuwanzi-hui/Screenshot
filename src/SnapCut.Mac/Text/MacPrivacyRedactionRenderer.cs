using SnapCut.Core;

namespace SnapCut.Mac.Text;

internal static class MacPrivacyRedactionRenderer
{
    public static PixelImage Apply(
        PixelImage source,
        IReadOnlyList<MacPrivacyCandidate> candidates)
    {
        var result = source.Clone();
        foreach (var candidate in candidates)
        {
            Pixelate(result, candidate.Bounds);
        }

        return result;
    }

    private static void Pixelate(PixelImage image, Avalonia.Rect bounds)
    {
        const int block = 10;
        var left = Math.Clamp((int)Math.Floor(bounds.Left) - 3, 0, image.Width);
        var top = Math.Clamp((int)Math.Floor(bounds.Top) - 3, 0, image.Height);
        var right = Math.Clamp((int)Math.Ceiling(bounds.Right) + 3, 0, image.Width);
        var bottom = Math.Clamp((int)Math.Ceiling(bounds.Bottom) + 3, 0, image.Height);
        for (var y = top; y < bottom; y += block)
        {
            for (var x = left; x < right; x += block)
            {
                var blockRight = Math.Min(right, x + block);
                var blockBottom = Math.Min(bottom, y + block);
                long blue = 0, green = 0, red = 0;
                var count = 0;
                for (var sourceY = y; sourceY < blockBottom; sourceY++)
                {
                    for (var sourceX = x; sourceX < blockRight; sourceX++)
                    {
                        var offset = (sourceY * image.Stride) + (sourceX * 4);
                        blue += image.Pixels[offset];
                        green += image.Pixels[offset + 1];
                        red += image.Pixels[offset + 2];
                        count++;
                    }
                }

                if (count > 0)
                {
                    image.FillRect(
                        x,
                        y,
                        blockRight - x,
                        blockBottom - y,
                        (byte)(blue / count),
                        (byte)(green / count),
                        (byte)(red / count));
                }
            }
        }
    }
}
