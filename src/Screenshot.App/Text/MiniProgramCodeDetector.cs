namespace Screenshot.App.Text;

/// <summary>Recognizes radial geometry only; never decodes its private payload.</summary>
internal static class MiniProgramCodeDetector
{
    private sealed record Ring(double X, double Y, double Radius);

    internal static RecognizedContentRegion? Find(byte[] pixels, int width, int height,
        CancellationToken cancellationToken)
    {
        var step = Math.Max(1, (int)Math.Ceiling(Math.Max(width, height) / 700d));
        var w = width / step;
        var h = height / step;
        if (w < 40 || h < 40) return null;
        var dark = new bool[w * h];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++) dark[y * w + x] = pixels[y * step * width + x * step] < 115;
        var visited = new bool[dark.Length];
        var queue = new int[dark.Length];
        var rings = new List<Ring>();
        for (var start = 0; start < dark.Length; start++)
        {
            if (start % w == 0) cancellationToken.ThrowIfCancellationRequested();
            if (visited[start] || !dark[start]) continue;
            var head = 0;
            var tail = 1;
            queue[0] = start;
            visited[start] = true;
            var left = start % w; var right = left;
            var top = start / w; var bottom = top;
            while (head < tail)
            {
                var index = queue[head++];
                var x = index % w; var y = index / w;
                left = Math.Min(left, x); right = Math.Max(right, x);
                top = Math.Min(top, y); bottom = Math.Max(bottom, y);
                for (var dy = -1; dy <= 1; dy++)
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        var nx = x + dx; var ny = y + dy;
                        if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                        var next = ny * w + nx;
                        if (visited[next] || !dark[next]) continue;
                        visited[next] = true;
                        queue[tail++] = next;
                    }
            }
            var cw = right - left + 1; var ch = bottom - top + 1;
            if (cw < 7 || ch < 7 || cw > 65 || ch > 65 || (double)cw / ch is < .7 or > 1.4) continue;
            var ring = new Ring((left + right) / 2d, (top + bottom) / 2d, (cw + ch) / 4d);
            // A bullseye has a separate central dot. Letters such as e/a have
            // a central stroke connected to the apparent outer ring instead.
            var centerIndex = (int)Math.Round(ring.Y) * w + (int)Math.Round(ring.X);
            if (queue.AsSpan(0, tail).Contains(centerIndex)) continue;
            if (Sample(ring.X, ring.Y) && RingDarkness(ring, .45) < .25 &&
                RingDarkness(ring, .83) > .65 && RingDarkness(ring, 1.2) < .3)
                rings.Add(ring);
            if (rings.Count >= 40) break;
        }
        foreach (var a in rings)
            foreach (var b in rings)
                foreach (var c in rings)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (ReferenceEquals(a, b) || ReferenceEquals(a, c) || ReferenceEquals(b, c)) continue;
                    var ab = Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));
                    var ac = Math.Sqrt(Math.Pow(a.X - c.X, 2) + Math.Pow(a.Y - c.Y, 2));
                    if (ab < a.Radius * 9 || Math.Abs(ab - ac) > ab * .25 ||
                        Math.Max(a.Radius, Math.Max(b.Radius, c.Radius)) > Math.Min(a.Radius, Math.Min(b.Radius, c.Radius)) * 1.5) continue;
                    var cosine = ((b.X - a.X) * (c.X - a.X) + (b.Y - a.Y) * (c.Y - a.Y)) / (ab * ac);
                    if (Math.Abs(cosine) > .22) continue;
                    var center = new Ring((b.X + c.X) / 2, (b.Y + c.Y) / 2, (ab + ac) / 2);
                    // Require data around the whole circle at multiple radii.
                    // A circle intersecting a few text lines is not a radial code;
                    // out-of-image samples must not count as its white modules.
                    var dataRadius = center.Radius * .94;
                    if (center.X - dataRadius < 0 || center.Y - dataRadius < 0 ||
                        center.X + dataRadius >= w || center.Y + dataRadius >= h) continue;
                    var bands = (HasRadialData(center, .68) ? 1 : 0) +
                        (HasRadialData(center, .80) ? 1 : 0) + (HasRadialData(center, .92) ? 1 : 0);
                    if (bands < 2) continue;
                    var radius = center.Radius * 1.04;
                    var x = Math.Max(0, center.X - radius) * step;
                    var y = Math.Max(0, center.Y - radius) * step;
                    return new RecognizedContentRegion(x, y,
                        Math.Min(width, (center.X + radius) * step) - x,
                        Math.Min(height, (center.Y + radius) * step) - y);
                }
        return null;

        bool Sample(double x, double y)
        {
            var px = (int)Math.Round(x); var py = (int)Math.Round(y);
            return px >= 0 && py >= 0 && px < w && py < h && dark[py * w + px];
        }
        double RingDarkness(Ring ring, double radius)
        {
            var hits = 0;
            for (var i = 0; i < 32; i++)
            {
                var angle = i * Math.Tau / 32;
                if (Sample(ring.X + Math.Cos(angle) * ring.Radius * radius,
                    ring.Y + Math.Sin(angle) * ring.Radius * radius)) hits++;
            }
            return hits / 32d;
        }
        bool HasRadialData(Ring ring, double radius)
        {
            var transitions = 0; var hits = 0; var previous = false;
            Span<int> quadrantHits = stackalloc int[4];
            Span<int> quadrantTransitions = stackalloc int[4];
            quadrantHits.Clear();
            quadrantTransitions.Clear();
            for (var i = 0; i <= 144; i++)
            {
                var angle = i * Math.Tau / 144;
                var current = Sample(ring.X + Math.Cos(angle) * ring.Radius * radius,
                    ring.Y + Math.Sin(angle) * ring.Radius * radius);
                if (i > 0 && current != previous)
                {
                    transitions++;
                    quadrantTransitions[(i - 1) / 36]++;
                }
                if (i < 144 && current)
                {
                    hits++;
                    quadrantHits[i / 36]++;
                }
                previous = current;
            }
            for (var quadrant = 0; quadrant < 4; quadrant++)
                if (quadrantHits[quadrant] < 3 || quadrantTransitions[quadrant] < 4) return false;
            return transitions >= 24 && hits is > 18 and < 95;
        }
    }
}
