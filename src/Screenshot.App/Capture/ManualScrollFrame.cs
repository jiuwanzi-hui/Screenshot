using System.Buffers;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Screenshot.App.Capture;

/// <summary>Reusable RGB pixels and textured row tiles for manual viewport registration.</summary>
internal sealed class ManualScrollFrame : IDisposable
{
    private readonly byte[] _pixels;
    private readonly ulong[][] _hashes;
    private bool _disposed;
    private const int BandCount = 8;
    internal int Width { get; }
    internal int Height { get; }
    internal long RetainedBytes => _pixels.LongLength + (long)BandCount * Height * sizeof(ulong);
    private (int Leading, int Trailing)? _blankEdges;
    internal bool IsBlank => BlankEdges.Leading == Height;
    internal Color BlankColor => Color.FromArgb(_pixels[2], _pixels[1], _pixels[0]);
    internal readonly record struct Match(int Shift, double Confidence);

    private (int Leading, int Trailing) BlankEdges
    {
        get
        {
            if (_blankEdges is { } cached) return cached;
            var leading = CountBlankRows(fromTop: true);
            var trailing = leading == Height ? Height : CountBlankRows(fromTop: false);
            return (_blankEdges = (leading, trailing)).Value;
        }
    }

    private int CountBlankRows(bool fromTop)
    {
        var right = Width - (Width >= 80 ? Math.Clamp(Width / 80, 10, 24) : 0);
        var reference = fromTop ? 0 : (Height - 1) * Width * 4;
        for (var offset = 0; offset < Height; offset++)
        {
            var y = fromTop ? offset : Height - offset - 1;
            for (var x = 0; x < right; x++)
                if (Difference(_pixels, reference, _pixels, (y * Width + x) * 4) > 2)
                    return offset;
        }
        return Height;
    }

    // Blank pixels verify continuity, but cannot measure displacement. Use
    // wheel travel only after observing an actual blank viewport, and require
    // every pixel of the proposed overlap to have the same background color.
    internal Match? LocateAcrossBlank(ManualScrollFrame current,
        ScrollCaptureDirection direction, int minimumOverlapRows, int? expectedRows)
    {
        if (Width != current.Width || Height != current.Height || !IsBlank && !current.IsBlank)
            return null;
        var down = direction == ScrollCaptureDirection.Down;
        var oldEdge = down ? (Height - 1) * Width * 4 : 0;
        var newEdge = down ? 0 : (Height - 1) * Width * 4;
        if (Difference(_pixels, oldEdge, current._pixels, newEdge) > 2) return null;
        var overlap = Math.Min(down ? BlankEdges.Trailing : BlankEdges.Leading,
            down ? current.BlankEdges.Leading : current.BlankEdges.Trailing);
        if (overlap < minimumOverlapRows) return null;
        var minimumShift = Height - overlap;
        // A changed exit frame also proves motion during the inertial tail.
        // An identical blank without new input proves no additional travel.
        var shift = Math.Max(minimumShift, Math.Min(expectedRows ?? 0, Height - minimumOverlapRows));
        if (shift > Height - minimumOverlapRows) return null;
        return new Match(down ? shift : -shift, 1);
    }

    internal ManualScrollFrame(Bitmap bitmap)
    {
        Width = bitmap.Width;
        Height = bitmap.Height;
        _pixels = ArrayPool<byte>.Shared.Rent(checked(Width * Height * 4));
        try
        {
            var data = bitmap.LockBits(new Rectangle(0, 0, Width, Height),
                ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                for (var y = 0; y < Height; y++)
                    Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), _pixels, y * Width * 4, Width * 4);
            }
            finally { bitmap.UnlockBits(data); }
        }
        catch
        {
            Dispose();
            throw;
        }

        _hashes = new ulong[BandCount][];
        // Exclude only a narrow scrollbar; a changing minimap is handled by
        // independent tiles rather than invalidating the entire row.
        var right = Width - (Width >= 80 ? Math.Clamp(Width / 80, 10, 24) : 0);
        for (var band = 0; band < BandCount; band++)
        {
            var left = band * right / BandCount;
            var end = (band + 1) * right / BandCount;
            var hashes = _hashes[band] = new ulong[Height];
            for (var y = 0; y < Height; y++)
            {
                var hash = 1469598103934665603UL;
                var textured = false;
                var first = (y * Width + left) * 4;
                for (var x = left; x < end; x++)
                {
                    var p = (y * Width + x) * 4;
                    textured |= Difference(_pixels, first, _pixels, p) >= 24;
                    for (var channel = 0; channel < 3; channel++)
                        hash = unchecked((hash ^ _pixels[p + channel]) * 1099511628211UL);
                }
                hashes[y] = textured ? hash : 0;
            }
        }
    }

    internal Match? Locate(ManualScrollFrame current, int minimumOverlapRows,
        int excludedTopRows = 0, int excludedBottomRows = 0)
    {
        if (Width != current.Width || Height != current.Height) return null;
        var top = excludedTopRows;
        var bottom = Height - excludedBottomRows;
        var sparse = HasFewTexturedRows(top, bottom, minimumOverlapRows / 2) ||
            current.HasFewTexturedRows(top, bottom, minimumOverlapRows / 2);
        var votes = new int[Height * 2 - 1];
        var uniqueVotes = new int[votes.Length];
        var firstRows = Enumerable.Repeat(int.MaxValue, votes.Length).ToArray();
        var lastRows = new int[votes.Length];
        for (var band = 0; band < BandCount; band++)
        {
            var locations = new Dictionary<ulong, List<int>>();
            var currentCounts = new Dictionary<ulong, int>();
            for (var y = top; y < bottom; y++)
            {
                var currentHash = current._hashes[band][y];
                if (currentHash != 0)
                    currentCounts[currentHash] = currentCounts.GetValueOrDefault(currentHash) + 1;
                var hash = _hashes[band][y];
                if (hash == 0) continue;
                if (!locations.TryGetValue(hash, out var rows)) locations.Add(hash, rows = []);
                rows.Add(y);
            }
            for (var y = top; y < bottom; y++)
            {
                if (!locations.TryGetValue(current._hashes[band][y], out var rows) || rows.Count > 8)
                    continue;
                foreach (var oldY in rows)
                {
                    var shift = oldY - y;
                    if (bottom - top - Math.Abs(shift) < minimumOverlapRows) continue;
                    var index = shift + Height - 1;
                    votes[index] += 8 / rows.Count;
                    if (rows.Count == 1 && currentCounts[current._hashes[band][y]] == 1)
                        uniqueVotes[index] += 8;
                    firstRows[index] = Math.Min(firstRows[index], y);
                    lastRows[index] = Math.Max(lastRows[index], y);
                }
            }
        }

        var candidates = Enumerable.Range(0, votes.Length)
            .Where(i => votes[i] >= 24 && uniqueVotes[i] >= 24 &&
                (lastRows[i] - firstRows[i] >= minimumOverlapRows / 2 || sparse))
            .OrderByDescending(i => votes[i]).Take(6)
            .Select(i => (Shift: i - Height + 1, Votes: votes[i], UniqueVotes: uniqueVotes[i],
                Score: sparse && IsExactOverlap(current, i - Height + 1, top, bottom)
                    ? 1 : Verify(current, i - Height + 1, top, bottom)))
            .Where(c => c.Score >= 0.94)
            .OrderByDescending(c => c.UniqueVotes * c.Score).ThenByDescending(c => c.Votes).ToArray();
        if (candidates.Length == 0) return null;
        var best = candidates[0];
        if (candidates.Length > 1 && best.Score - candidates[1].Score < 0.01 &&
            best.UniqueVotes < candidates[1].UniqueVotes * 2 &&
            best.Votes < candidates[1].Votes * 2) return null;
        return new Match(best.Shift, best.Score);
    }

    private bool HasFewTexturedRows(int top, int bottom, int maximumRows)
    {
        var count = 0;
        for (var y = top; y < bottom; y++)
            if (_hashes.Any(band => band[y] != 0) && ++count > maximumRows) return false;
        return count > 0;
    }

    internal bool IsExactOverlap(ManualScrollFrame current, int shift, int contentTop, int contentBottom)
    {
        // A few newly appearing text pixels can be a unique anchor, even when
        // the normal distributed-text evidence has not yet become visible.
        // Accept them only with an exact check of the entire overlapping body.
        var top = Math.Max(contentTop, contentTop - shift);
        var bottom = Math.Min(contentBottom, contentBottom - shift);
        var right = Width - (Width >= 80 ? Math.Clamp(Width / 80, 10, 24) : 0);
        for (var y = top; y < bottom; y++)
        for (var x = 0; x < right; x++)
            if (Difference(_pixels, ((y + shift) * Width + x) * 4,
                    current._pixels, (y * Width + x) * 4) > 2) return false;
        return true;
    }

    internal bool IsConsistentAtShift(ManualScrollFrame current, int shift) =>
        Math.Abs(shift) < Height &&
        (IsExactOverlap(current, shift, 0, Height) || Verify(current, shift, 0, Height) >= 0.94);

    private double Verify(ManualScrollFrame current, int shift, int contentTop, int contentBottom)
    {
        var top = Math.Max(contentTop, contentTop - shift);
        var bottom = Math.Min(contentBottom, contentBottom - shift);
        var right = Width - (Width >= 80 ? Math.Clamp(Width / 80, 10, 24) : 0);
        const int tileHeight = 24;
        var rowCount = (bottom - top + tileHeight - 1) / tileHeight;
        var tiles = new TileEvidence[BandCount * rowCount];
        var firstBandMatchingRows = 0;
        for (var band = 0; band < BandCount; band++)
        {
            var left = band * right / BandCount;
            var end = (band + 1) * right / BandCount;
            for (var row = 0; row < rowCount; row++)
            {
                var edges = 0;
                var matching = 0;
                var rowTop = top + row * tileHeight;
                for (var y = rowTop; y < Math.Min(bottom - 1, rowTop + tileHeight); y += 2)
                {
                    for (var x = left; x < end - 1; x += 2)
                    {
                        var oldP = ((y + shift) * Width + x) * 4;
                        var newP = (y * Width + x) * 4;
                        var texture = Math.Max(
                            Difference(_pixels, oldP, _pixels, oldP + 4),
                            Difference(current._pixels, newP, current._pixels, newP + 4));
                        if (texture < 18) continue;
                        edges++;
                        if (Difference(_pixels, oldP, current._pixels, newP) <= 24) matching++;
                    }
                }
                tiles[band * rowCount + row] = new TileEvidence(edges, matching);
                if (band == 0 && edges >= 8 && matching >= edges * 0.94)
                    firstBandMatchingRows++;
            }
        }

        double totalWeight = 0, matchedWeight = 0;
        var totalEdges = 0;
        var matchingTiles = 0;
        var interiorMatchingTiles = 0;
        var firstMatchingRow = int.MaxValue;
        var lastMatchingRow = 0;
        for (var band = 0; band < BandCount; band++)
        {
            var inconsistentRows = 0;
            for (var row = 0; row < rowCount; row++)
            {
                var tile = tiles[band * rowCount + row];
                if (tile.Edges >= 8 && tile.Score < 0.85) inconsistentRows++;
            }
            // Ignore a full-height editor minimap only with continuous gutter
            // evidence. Never discard an ordinary right-hand chat column:
            // repeated avatars must not overrule different message text.
            if (band >= BandCount - 2 && inconsistentRows >= rowCount * 0.8 &&
                firstBandMatchingRows >= rowCount * 0.65) continue;
            for (var row = 0; row < rowCount; row++)
            {
                var tile = tiles[band * rowCount + row];
                if (tile.Edges < 8) continue;
                // Once a tile has enough texture, its spatial support matters
                // more than edge density. A dense animated sticker must not
                // outweigh many matching tiles of thin, aliased text.
                const int weight = 1;
                totalEdges += tile.Edges;
                totalWeight += weight;
                matchedWeight += weight * tile.Score;
                if (tile.Score < 0.94) continue;
                matchingTiles++;
                if (band is > 0 and < BandCount - 1) interiorMatchingTiles++;
                firstMatchingRow = Math.Min(firstMatchingRow, row * tileHeight);
                lastMatchingRow = Math.Max(lastMatchingRow, row * tileHeight);
            }
        }
        if (totalEdges < 64 || totalWeight == 0) return 0;
        var score = matchedWeight / totalWeight;
        if (score >= 0.94) return score;
        // Bound the influence of dense animated stickers by spatial tiles.
        // A relaxed registration still needs distributed, pixel-exact body
        // anchors, plus candidate uniqueness in Locate. Blank pixels and a
        // single repeated avatar cannot provide those anchors.
        var requiredSpan = Math.Min(96, (bottom - top) / 3);
        return score >= 0.65 && matchingTiles >= 6 && interiorMatchingTiles >= 2 &&
            lastMatchingRow - firstMatchingRow >= requiredSpan
            ? 0.94 + (score - 0.65) * 0.1 : 0;
    }

    private readonly record struct TileEvidence(int Edges, int Matching)
    {
        internal double Score => Edges == 0 ? 0 : Matching / (double)Edges;
    }

    internal (int Top, int Bottom)? FindFixedRegions(ManualScrollFrame current, int minimumOverlapRows)
    {
        if (Width != current.Width || Height != current.Height) return null;
        var stationary = new bool[Height];
        var right = Width - (Width >= 80 ? Math.Clamp(Width / 80, 10, 24) : 0);
        var step = Math.Max(1, right / 320);
        for (var y = 0; y < Height; y++)
        {
            var changed = 0;
            var samples = 0;
            for (var x = 0; x < right; x += step)
            {
                var p = (y * Width + x) * 4;
                if (Difference(_pixels, p, current._pixels, p) > 12) changed++;
                samples++;
            }
            stationary[y] = changed * 200 <= samples;
        }

        var top = 0;
        while (top < Height && stationary[top]) top++;
        // An unchanged viewport is not evidence of either a fixed bar or motion.
        if (top == Height) return null;
        var bottom = 0;
        while (bottom < Height - top && stationary[Height - bottom - 1]) bottom++;
        top = RefineFixedEdge(top, fromTop: true, right, step);
        bottom = RefineFixedEdge(bottom, fromTop: false, right, step);
        if (top == 0 && bottom == 0 || Height - top - bottom <= minimumOverlapRows) return null;

        // Require a unique, verified nonzero displacement in the remaining
        // content. A blinking caret, a spinner or an all-blank page cannot
        // establish a fixed region, even when most screen rows stay unchanged.
        return Locate(current, minimumOverlapRows, top, bottom) is { Shift: not 0 }
            ? (top, bottom) : null;
    }

    private int RefineFixedEdge(int rows, bool fromTop, int right, int step)
    {
        if (rows < 8) return 0;
        var start = fromTop ? 0 : Height - rows;
        var end = fromTop ? rows : Height;
        var texturedRows = 0;
        for (var y = start; y < end; y++)
            if (_hashes.Any(band => band[y] != 0)) texturedRows++;
        // In particular, do not remove a stationary-looking blank margin in
        // a scrolling document. There must be actual controls/text at the edge.
        if (texturedRows < 2) return 0;

        // Empty chat space can share the same screen coordinates in both
        // frames. Prefer a full-width divider/background transition inside
        // that space so the blank document rows remain part of the content.
        for (var distance = rows; distance >= 8; distance--)
        {
            var y = fromTop ? distance : Height - distance;
            if (y <= 0 || y >= Height) continue;
            var changed = 0;
            var samples = 0;
            for (var x = 0; x < right; x += step)
            {
                if (Difference(_pixels, ((y - 1) * Width + x) * 4,
                    _pixels, (y * Width + x) * 4) >= 3) changed++;
                samples++;
            }
            if (changed * 5 >= samples * 4) return distance;
        }
        return rows;
    }

    private static int Difference(byte[] a, int p, byte[] b, int q) =>
        Math.Abs(a[p] - b[q]) + Math.Abs(a[p + 1] - b[q + 1]) + Math.Abs(a[p + 2] - b[q + 2]);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ArrayPool<byte>.Shared.Return(_pixels);
    }
}
