using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Screenshot.App.Capture;

/// <summary>
/// Fast path for pixel-stable documents. Row hashes only nominate shifts;
/// every overlapping pixel is then compared, and ambiguous shifts are refused.
/// </summary>
internal static class ExactScrollFrameMatcher
{
    internal readonly record struct Match(int Shift, bool HasNewTexturedRows);

    internal static Match? Find(Bitmap previous, Bitmap current, int minimumOverlapRows)
    {
        if (previous.Size != current.Size ||
            previous.PixelFormat != PixelFormat.Format32bppPArgb ||
            current.PixelFormat != PixelFormat.Format32bppPArgb)
        {
            return null;
        }

        // Keep the entire text/gutter area; only omit the scrollbar strip.
        var width = previous.Width >= 80
            ? previous.Width - Math.Clamp(previous.Width / 80, 10, 24)
            : previous.Width;
        var rowBytes = width * 4;
        var height = previous.Height;
        var before = ReadRows(previous, rowBytes);
        var after = ReadRows(current, rowBytes);
        var beforeHashes = HashRows(before, rowBytes);
        var afterHashes = HashRows(after, rowBytes);
        var rowLocations = new Dictionary<ulong, List<int>>();
        for (var y = 0; y < height; y++)
        {
            if (beforeHashes[y] == 0) continue;
            if (!rowLocations.TryGetValue(beforeHashes[y], out var rows))
            {
                rows = [];
                rowLocations.Add(beforeHashes[y], rows);
            }
            rows.Add(y);
        }

        var votes = new Dictionary<int, int>();
        for (var y = 0; y < height; y++)
        {
            if (!rowLocations.TryGetValue(afterHashes[y], out var rows)) continue;
            foreach (var oldY in rows)
            {
                var shift = oldY - y;
                if (height - Math.Abs(shift) >= minimumOverlapRows)
                    votes[shift] = votes.GetValueOrDefault(shift) + 1;
            }
        }

        int? verifiedShift = null;
        foreach (var (shift, count) in votes)
        {
            if (count < Math.Min(8, minimumOverlapRows)) continue;
            var oldTop = Math.Max(0, shift);
            var newTop = Math.Max(0, -shift);
            var length = (height - Math.Abs(shift)) * rowBytes;
            if (!before.AsSpan(oldTop * rowBytes, length)
                    .SequenceEqual(after.AsSpan(newTop * rowBytes, length))) continue;
            if (verifiedShift is not null) return null;
            verifiedShift = shift;
        }

        if (verifiedShift is not { } result) return null;
        var firstNewRow = result > 0 ? height - result : 0;
        var newRowCount = Math.Abs(result);
        var novelRows = 0;
        for (var y = firstNewRow; y < firstNewRow + newRowCount; y++)
        {
            if (afterHashes[y] != 0 && !rowLocations.ContainsKey(afterHashes[y])) novelRows++;
        }
        return new Match(result, novelRows >= Math.Min(4, newRowCount));
    }

    private static byte[] ReadRows(Bitmap bitmap, int rowBytes)
    {
        var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height),
            ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        try
        {
            var pixels = new byte[rowBytes * bitmap.Height];
            for (var y = 0; y < bitmap.Height; y++)
                Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), pixels, y * rowBytes, rowBytes);
            return pixels;
        }
        finally { bitmap.UnlockBits(data); }
    }

    private static ulong[] HashRows(byte[] pixels, int rowBytes)
    {
        var hashes = new ulong[pixels.Length / rowBytes];
        for (var y = 0; y < hashes.Length; y++)
        {
            var row = pixels.AsSpan(y * rowBytes, rowBytes);
            var hash = 1469598103934665603UL;
            var textured = false;
            for (var x = 0; x < rowBytes; x += 4)
            {
                textured |= Math.Abs(row[x] - row[0]) + Math.Abs(row[x + 1] - row[1]) +
                    Math.Abs(row[x + 2] - row[2]) >= 24;
                hash = unchecked((hash ^ row[x]) * 1099511628211UL);
                hash = unchecked((hash ^ row[x + 1]) * 1099511628211UL);
                hash = unchecked((hash ^ row[x + 2]) * 1099511628211UL);
            }
            hashes[y] = textured ? hash : 0;
        }
        return hashes;
    }
}
