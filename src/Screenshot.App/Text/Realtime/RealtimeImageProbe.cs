using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Screenshot.App.Text.Realtime;

// Compact samples are only a change detector. OCR always receives original pixels.
internal sealed record RealtimeImageProbe(int Width, int Height, int Columns, byte[] Samples)
{
    internal static RealtimeImageProbe Read(Bitmap bitmap, Rectangle? area = null)
    {
        var rect = Rectangle.Intersect(area ?? new(0, 0, bitmap.Width, bitmap.Height),
            new(0, 0, bitmap.Width, bitmap.Height));
        if (rect.Width < 1 || rect.Height < 1) return new(0, 0, 0, []);
        var columns = Math.Min(area is null ? 640 : 160, rect.Width);
        var rows = Math.Min(area is null ? 360 : 80, rect.Height);
        var samples = new byte[columns * rows];
        var data = bitmap.LockBits(new(0, 0, bitmap.Width, bitmap.Height),
            ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (var y = 0; y < rows; y++)
            for (var x = 0; x < columns; x++)
            {
                var color = Marshal.ReadInt32(data.Scan0 +
                    (rect.Top + y * rect.Height / rows) * data.Stride +
                    (rect.Left + x * rect.Width / columns) * 4);
                samples[y * columns + x] = (byte)((((color >> 16) & 255) * 77 +
                    ((color >> 8) & 255) * 150 + (color & 255) * 29) >> 8);
            }
        }
        finally { bitmap.UnlockBits(data); }
        return new(rect.Width, rect.Height, columns, samples);
    }

    internal bool Matches(RealtimeImageProbe other, double changedFraction = 0)
    {
        if (Width != other.Width || Height != other.Height || Samples.Length != other.Samples.Length) return false;
        var changed = 0;
        for (var i = 0; i < Samples.Length; i++)
            if (Math.Abs(Samples[i] - other.Samples[i]) > 22) changed++;
        return changed <= Samples.Length * changedFraction;
    }

    internal RealtimeImageProbe Crop(Rectangle area)
    {
        if (Columns == 0 || Width == 0 || Height == 0) return new(0, 0, 0, []);
        var rows = Samples.Length / Columns;
        var left = Math.Clamp((int)Math.Floor(area.Left * (double)Columns / Width), 0, Columns);
        var top = Math.Clamp((int)Math.Floor(area.Top * (double)rows / Height), 0, rows);
        var right = Math.Clamp((int)Math.Ceiling(area.Right * (double)Columns / Width), left, Columns);
        var bottom = Math.Clamp((int)Math.Ceiling(area.Bottom * (double)rows / Height), top, rows);
        var width = right - left; var height = bottom - top;
        var samples = new byte[width * height];
        for (var y = 0; y < height; y++) Array.Copy(Samples, (top + y) * Columns + left, samples, y * width, width);
        return new(width, height, width, samples);
    }

    internal bool MatchesGlyphs(RealtimeImageProbe other)
    {
        if (Width != other.Width || Height != other.Height || Samples.Length != other.Samples.Length) return false;
        var columns = Columns;
        var edges = 0; var retained = 0;
        for (var i = 1; i < Samples.Length; i++)
        {
            if (i % columns == 0) continue;
            var edge = Samples[i] - Samples[i - 1];
            if (Math.Abs(edge) < 45) continue;
            edges++;
            var next = other.Samples[i] - other.Samples[i - 1];
            if (Math.Sign(next) == Math.Sign(edge) && Math.Abs(next) >= 25) retained++;
        }
        // Follow glyph edges, not brightness changes behind transparent game subtitles.
        return edges >= 8 ? retained >= edges * .7 : Matches(other, .12);
    }
}
