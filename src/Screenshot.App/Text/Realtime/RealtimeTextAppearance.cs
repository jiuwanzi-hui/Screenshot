using System.Drawing;

namespace Screenshot.App.Text.Realtime;

// Only colors survive OCR. Never retain a source bitmap in cached translations.
internal sealed record RealtimeTextAppearance(int Background, int Foreground)
{
    internal static RealtimeTextAppearance Read(Bitmap bitmap, OcrTextRegion region)
    {
        var left = Math.Clamp((int)Math.Floor(region.X), 0, bitmap.Width - 1);
        var top = Math.Clamp((int)Math.Floor(region.Y), 0, bitmap.Height - 1);
        var right = Math.Clamp((int)Math.Ceiling(region.X + region.Width), left + 1, bitmap.Width);
        var bottom = Math.Clamp((int)Math.Ceiling(region.Y + region.Height), top + 1, bitmap.Height);
        var colors = new Dictionary<int, (int Count, long Red, long Green, long Blue)>();
        for (var y = top; y < bottom; y += Math.Max(1, (bottom - top) / 12))
        for (var x = left; x < right; x += Math.Max(1, (right - left) / 24))
        {
            var pixel = bitmap.GetPixel(x, y);
            var key = (pixel.R >> 4 << 8) | (pixel.G >> 4 << 4) | (pixel.B >> 4);
            var entry = colors.GetValueOrDefault(key);
            colors[key] = (entry.Count + 1, entry.Red + pixel.R, entry.Green + pixel.G, entry.Blue + pixel.B);
        }
        // Match screenshot translation: dominant background, not an average
        // polluted by dark/colored source glyphs. Covering is always opaque.
        var dominant = colors.MaxBy(entry => entry.Value.Count).Value;
        var background = Color.FromArgb((int)(dominant.Red / dominant.Count),
            (int)(dominant.Green / dominant.Count), (int)(dominant.Blue / dominant.Count));
        var light = .2126 * background.R + .7152 * background.G + .0722 * background.B >= 145;
        return new(background.ToArgb(), (light ? Color.FromArgb(25, 32, 36) : Color.FromArgb(248, 251, 251)).ToArgb());
    }
}
