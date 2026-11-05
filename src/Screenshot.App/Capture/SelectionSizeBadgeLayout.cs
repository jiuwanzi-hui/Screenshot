using System.Windows;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace Screenshot.App.Capture;

// Shared by the native live badge and the WPF editor badge. All inputs use
// the caller's coordinate system (physical pixels or device-independent units).
internal static class SelectionSizeBadgeLayout
{
    internal static Rect Place(Rect selection, Size size, Rect screen,
        ReadOnlySpan<Rect> obstacles, double gap = 6)
    {
        if (screen.IsEmpty || size.Width <= 0 || size.Height <= 0 ||
            size.Width > screen.Width || size.Height > screen.Height) return Rect.Empty;

        var left = Math.Clamp(selection.Left, screen.Left, screen.Right - size.Width);
        var right = Math.Clamp(selection.Right - size.Width, screen.Left, screen.Right - size.Width);
        var top = Math.Clamp(selection.Top, screen.Top, screen.Bottom - size.Height);
        var bottom = Math.Clamp(selection.Bottom - size.Height, screen.Top, screen.Bottom - size.Height);
        ReadOnlySpan<Point> preferred =
        [
            new(left, selection.Top - size.Height - gap),
            new(right, selection.Top - size.Height - gap),
            new(left, selection.Bottom + gap),
            new(right, selection.Bottom + gap),
            new(selection.Right + gap, top), new(selection.Left - size.Width - gap, top),
            new(selection.Right + gap, bottom), new(selection.Left - size.Width - gap, bottom),
        ];
        foreach (var point in preferred)
        {
            var candidate = new Rect(point, size);
            if (IsAvailable(candidate, screen, obstacles, gap)) return candidate;
        }

        // When both outside edges are occupied, search the free rectangles
        // formed by screen/selection/panel edges. This also handles full-screen
        // selections and manually moved or expanded toolbars.
        Span<double> xs = stackalloc double[6 + obstacles.Length * 2];
        Span<double> ys = stackalloc double[6 + obstacles.Length * 2];
        xs[0] = screen.Left; xs[1] = screen.Right - size.Width;
        xs[2] = left; xs[3] = right;
        xs[4] = selection.Left + gap; xs[5] = selection.Right - size.Width - gap;
        ys[0] = screen.Top; ys[1] = screen.Bottom - size.Height;
        ys[2] = top; ys[3] = bottom;
        ys[4] = selection.Top + gap; ys[5] = selection.Bottom - size.Height - gap;
        for (var index = 0; index < obstacles.Length; index++)
        {
            var obstacle = obstacles[index];
            xs[6 + index * 2] = obstacle.Left - size.Width - gap;
            xs[7 + index * 2] = obstacle.Right + gap;
            ys[6 + index * 2] = obstacle.Top - size.Height - gap;
            ys[7 + index * 2] = obstacle.Bottom + gap;
        }
        var best = Rect.Empty;
        var bestScore = double.PositiveInfinity;
        foreach (var x in xs)
        foreach (var y in ys)
        {
            var candidate = new Rect(x, y, size.Width, size.Height);
            if (!IsAvailable(candidate, screen, obstacles, gap)) continue;
            var overlap = Rect.Intersect(candidate, selection);
            var score = Math.Abs(x - left) + Math.Abs(y - (selection.Top - size.Height - gap));
            if (!overlap.IsEmpty) score += overlap.Width * overlap.Height * 4;
            if (score >= bestScore) continue;
            best = candidate;
            bestScore = score;
        }
        // On an exceptionally small display there may be no free space at
        // all. Omit the informational badge instead of covering an action.
        return best;
    }

    private static bool IsAvailable(Rect candidate, Rect screen, ReadOnlySpan<Rect> obstacles, double gap)
    {
        if (!screen.Contains(candidate)) return false;
        foreach (var obstacle in obstacles)
        {
            if (obstacle.IsEmpty) continue;
            var inflated = obstacle;
            inflated.Inflate(gap, gap);
            var overlap = Rect.Intersect(candidate, inflated);
            if (!overlap.IsEmpty && overlap.Width > .01 && overlap.Height > .01) return false;
        }
        return true;
    }
}
