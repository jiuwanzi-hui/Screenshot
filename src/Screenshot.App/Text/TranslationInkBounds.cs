using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Screenshot.App.Text;

// OCR detection rectangles include padding and occasionally the next line. Use
// the selected image once, on the worker, to measure the text rather than that padding.
internal static class TranslationInkBounds
{
    internal static IReadOnlyList<OcrTextRegion> Refine(Bitmap bitmap, IReadOnlyList<OcrTextRegion> regions)
    {
        var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height),
            ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var stride = Math.Abs(data.Stride);
            var pixels = new byte[stride * bitmap.Height];
            for (var y = 0; y < bitmap.Height; y++)
                Marshal.Copy(data.Scan0 + y * data.Stride, pixels, y * stride, stride);
            return regions.Select(region => Refine(region, pixels, stride, bitmap.Width, bitmap.Height)).ToArray();
        }
        finally { bitmap.UnlockBits(data); }
    }

    private static OcrTextRegion Refine(OcrTextRegion region, byte[] pixels, int stride, int width, int height)
    {
        var left = Math.Clamp((int)Math.Floor(region.X), 0, width - 1);
        var right = Math.Clamp((int)Math.Ceiling(region.X + region.Width), left + 1, width);
        var top = Math.Clamp((int)Math.Floor(region.Y), 0, height - 1);
        var bottom = Math.Clamp((int)Math.Ceiling(region.Y + region.Height), top + 1, height);
        var colors = new Dictionary<int, int>();
        for (var y = top; y < bottom; y += 2)
            for (var x = left; x < right; x += 3)
            {
                var p = y * stride + x * 4;
                var key = (pixels[p] >> 4) | (pixels[p + 1] >> 4 << 4) | (pixels[p + 2] >> 4 << 8);
                colors[key] = colors.GetValueOrDefault(key) + 1;
            }
        if (colors.Count == 0) return region;
        var background = colors.MaxBy(entry => entry.Value).Key;
        var blue = (background & 15) * 16 + 8;
        var green = ((background >> 4) & 15) * 16 + 8;
        var red = ((background >> 8) & 15) * 16 + 8;
        bool IsInk(int x, int y)
        {
            var p = y * stride + x * 4;
            return Math.Abs(pixels[p] - blue) + Math.Abs(pixels[p + 1] - green) +
                   Math.Abs(pixels[p + 2] - red) >= 100;
        }
        var runs = new List<(int Top, int Bottom)>();
        var start = -1;
        for (var y = top; y <= bottom; y++)
        {
            var count = 0;
            if (y < bottom)
                for (var x = left; x < right; x++)
                    if (IsInk(x, y)) count++;
            var ink = count >= Math.Max(2, (right - left) / 160);
            if (ink && start < 0) start = y;
            if (!ink && start >= 0)
            {
                if (y - start >= 3) runs.Add((start, y));
                start = -1;
            }
        }
        if (runs.Count == 0) return region;
        var center = region.Y + region.Height / 2;
        var run = runs.MinBy(run => Math.Abs((run.Top + run.Bottom) / 2d - center));
        // Detached accents/dots immediately above the main glyph belong to it.
        foreach (var other in runs)
            if (other.Bottom <= run.Top && run.Top - other.Bottom <= 2)
                run.Top = other.Top;
        var inkHeight = run.Bottom - run.Top;
        if (inkHeight < 5) return region;
        // A paragraph box can legitimately contain multiple complete rows.
        // Preserve those rows instead of assigning all its text to the central
        // row. Require both repeated glyph heights and enough text for wrapping,
        // so padding which happens to include a neighbour stays single-line.
        var rows = runs.Where(item => item.Bottom - item.Top >= inkHeight * 0.65).ToArray();
        var estimatedAdvance = region.Text.Sum(character => character is >= '\u2e80' and <= '\uffef'
            ? inkHeight : char.IsWhiteSpace(character) ? inkHeight * 0.3 : inkHeight * 0.5);
        if (rows.Length >= 2 && estimatedAdvance > (right - left) * 1.25)
        {
            run = (rows[0].Top, rows[^1].Bottom);
            inkHeight = rows.Select(item => item.Bottom - item.Top).Order().ElementAt(rows.Length / 2);
        }
        var yTop = Math.Max(0, run.Top - 2);
        return region with
        {
            Y = yTop,
            Height = Math.Min(height, run.Bottom + 2) - yTop,
            EstimatedFontSize = Math.Clamp(inkHeight * 1.1, 8, 64),
        };
    }
}
