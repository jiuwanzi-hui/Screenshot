using System.Drawing;

namespace Screenshot.App.Text;

public static partial class TableRecognitionService
{
    private static void FindGridAxes(BitmapPixelBuffer pixels, IReadOnlyList<OcrWordRegion> words,
        out List<AxisLine> vertical, out List<AxisLine> horizontal)
    {
        var typicalHeight = words.Count == 0 ? 18 : words.Select(word => word.Height).Order().ElementAt(words.Count / 2);
        // A separator belonging to one merged header must not disappear simply
        // because the capture contains many rows below it. Measure the minimum
        // run against text size, not a percentage of the entire image height.
        var minimumRun = Math.Clamp((int)Math.Ceiling(typicalHeight * 0.65), 6, 20);
        vertical = FindGridLines(pixels, vertical: true, minimumRun);
        horizontal = FindGridLines(pixels, vertical: false, minimumRun);
        RemoveTextStrokeLines(vertical, horizontal, words);
        AddOpenBoundaries(vertical, horizontal, pixels.Width);
        AddOpenBoundaries(horizontal, vertical, pixels.Height);
        // Virtual outer edges also retain full-width header rules which have no
        // physical intersection with a merged header's internal columns.
        FilterGridIntersections(pixels, vertical, horizontal);
        AddInferredRegularDetailBoundaries(pixels, vertical, horizontal, words);
        if (vertical.Count == 2 && horizontal.Count >= 3)
        {
            AddUnruledColumns(vertical, horizontal, words);
            if (vertical.Count >= 3 && horizontal.Count == 3)
                AddUnruledRows(horizontal, vertical, words);
        }
    }

    private static void AddOpenBoundaries(List<AxisLine> lines, List<AxisLine> perpendicular, int axisLength)
    {
        var candidates = perpendicular.Where(line => !line.Inferred && line.RunEnd - line.RunStart >= 24).ToArray();
        if (candidates.Length < 2) return;
        var longest = candidates.Max(line => line.RunEnd - line.RunStart);
        candidates = candidates.Where(line => line.RunEnd - line.RunStart >= longest * 0.5).ToArray();
        if (candidates.Length < 2) return;
        Add(candidates.Min(line => line.RunStart));
        Add(candidates.Max(line => line.RunEnd));
        lines.Sort((first, second) => first.Position.CompareTo(second.Position));

        void Add(int position)
        {
            position = position <= 3 ? 0 : position >= axisLength - 4 ? axisLength - 1 : position;
            if (lines.Count > 0 && position > lines[0].Position + 4 && position < lines[^1].Position - 4) return;
            if (lines.Any(line => Math.Abs(line.Position - position) <= 4)) return;
            lines.Add(new AxisLine(position, int.MaxValue, 0, 0, Inferred: true));
            lines.Sort((first, second) => first.Position.CompareTo(second.Position));
        }
    }

    private static void AddUnruledColumns(List<AxisLine> vertical, List<AxisLine> horizontal,
        IReadOnlyList<OcrWordRegion> words)
    {
        var inside = words.Where(word => word.Y + word.Height / 2 >= horizontal[0].Position &&
            word.Y + word.Height / 2 <= horizontal[^1].Position &&
            word.X >= vertical[0].Position && word.X + word.Width <= vertical[^1].Position).ToArray();
        if (inside.Length < 4) return;
        var height = Math.Max(6, inside.Select(word => word.Height).Order().ElementAt(inside.Length / 2));
        var rows = BuildRows(inside, height);
        var gaps = rows.SelectMany((row, index) =>
        {
            var fragments = MergeNearbyWords(row, height);
            return fragments.Zip(fragments.Skip(1), (left, right) =>
                (Left: left.Right + 2, Right: right.X - 2, Row: index));
        }).Where(gap => gap.Right - gap.Left >= height).ToArray();
        var required = Math.Max(2, (int)Math.Ceiling(rows.Count * 0.5));
        var used = new HashSet<int>();
        foreach (var candidate in gaps.Select(gap => (gap.Left + gap.Right) / 2).Order())
        {
            var supporters = gaps.Select((gap, index) => (gap, index)).Where(item =>
                !used.Contains(item.index) && candidate >= item.gap.Left && candidate <= item.gap.Right).ToArray();
            if (supporters.Select(item => item.gap.Row).Distinct().Count() < required) continue;
            var position = (int)Math.Round((supporters.Max(item => item.gap.Left) + supporters.Min(item => item.gap.Right)) / 2);
            vertical.Add(new AxisLine(position, int.MaxValue, 0, 0, Inferred: true));
            foreach (var supporter in supporters) used.Add(supporter.index);
        }
        vertical.Sort((first, second) => first.Position.CompareTo(second.Position));
    }

    private static void AddUnruledRows(List<AxisLine> horizontal, List<AxisLine> vertical,
        IReadOnlyList<OcrWordRegion> words)
    {
        var inferred = new List<AxisLine>();
        for (var band = 0; band < horizontal.Count - 1; band++)
        {
            var inside = words.Where(word => word.Y >= horizontal[band].Position &&
                word.Y + word.Height <= horizontal[band + 1].Position).ToArray();
            if (inside.Length < 4) continue;
            var height = Math.Max(6, inside.Select(word => word.Height).Order().ElementAt(inside.Length / 2));
            var rows = BuildRows(inside, height);
            for (var row = 1; row < rows.Count; row++)
            {
                var previous = rows[row - 1];
                var next = rows[row];
                if (OccupiedColumns(previous) < 2 || OccupiedColumns(next) < 2) continue;
                var bottom = previous.Max(word => word.Y + word.Height);
                var top = next.Min(word => word.Y);
                if (top - bottom < height * 0.3) continue;
                inferred.Add(new AxisLine((int)Math.Round((top + bottom) / 2), int.MaxValue, 0, 0, Inferred: true));
            }
        }
        horizontal.AddRange(inferred);
        horizontal.Sort((first, second) => first.Position.CompareTo(second.Position));

        int OccupiedColumns(IEnumerable<OcrWordRegion> row) => row.Select(word =>
            vertical.FindLastIndex(line => line.Position <= word.X + word.Width / 2)).Distinct().Count();
    }

    private static List<int[]> BuildCellRectangles(BitmapPixelBuffer pixels, List<AxisLine> vertical,
        List<AxisLine> horizontal, IReadOnlyList<OcrWordRegion> words)
    {
        var rows = horizontal.Count - 1;
        var columns = vertical.Count - 1;
        var verticalSeparators = new bool[rows, columns + 1];
        var horizontalSeparators = new bool[rows + 1, columns];
        for (var row = 0; row < rows; row++)
        for (var column = 1; column < columns; column++)
        {
            var line = vertical[column];
            verticalSeparators[row, column] = line.Inferred
                ? !words.Any(word => word.X + 2 < line.Position && word.X + word.Width - 2 > line.Position &&
                    word.Y + word.Height / 2 > horizontal[row].Position && word.Y + word.Height / 2 < horizontal[row + 1].Position)
                : HasVerticalSeparator(pixels, line.Position, horizontal[row].Position, horizontal[row + 1].Position, words);
        }
        for (var row = 1; row < rows; row++)
        for (var column = 0; column < columns; column++)
            horizontalSeparators[row, column] = horizontal[row].Inferred
                ? !words.Any(word => word.Y + 2 < horizontal[row].Position && word.Y + word.Height - 2 > horizontal[row].Position &&
                    word.X + word.Width / 2 > vertical[column].Position && word.X + word.Width / 2 < vertical[column + 1].Position)
                : HasHorizontalSeparator(pixels, horizontal[row].Position, vertical[column].Position, vertical[column + 1].Position, words);
        return PartitionCells(rows, columns, verticalSeparators, horizontalSeparators);
    }

    // Flood-fill through missing borders can travel around an intact border,
    // forming an L-shaped component. Only merge rectangular areas whose entire
    // interior is open; never split a flood-filled component across a real rule.
    internal static List<int[]> PartitionCells(int rows, int columns, bool[,] vertical, bool[,] horizontal)
    {
        var occupied = new bool[rows, columns];
        var result = new List<int[]>();
        for (var row = 0; row < rows; row++)
        for (var column = 0; column < columns; column++)
        {
            if (occupied[row, column]) continue;
            var right = column + 1;
            while (right < columns && !occupied[row, right] && !vertical[row, right]) right++;
            var bottom = row + 1;
            while (bottom < rows)
            {
                var canExtend = true;
                for (var x = column; x < right; x++)
                    if (occupied[bottom, x] || horizontal[bottom, x] || x > column && vertical[bottom, x])
                        canExtend = false;
                if (!canExtend) break;
                bottom++;
            }
            var members = new List<int>();
            for (var y = row; y < bottom; y++)
            for (var x = column; x < right; x++)
            {
                occupied[y, x] = true;
                members.Add(y * columns + x);
            }
            result.Add(members.ToArray());
        }
        return result;
    }

    internal static IReadOnlyList<Rectangle> FindCellBounds(Bitmap bitmap, IReadOnlyList<OcrWordRegion> words, CancellationToken cancellationToken = default)
    {
        if (bitmap.Width < 32 || bitmap.Height < 32) return [];
        using var pixels = BitmapPixelBuffer.Create(bitmap, cancellationToken);
        FindGridAxes(pixels, words, out var vertical, out var horizontal);
        if (vertical.Count is < 3 or > 81 || horizontal.Count is < 3 or > 201) return [];
        var columns = vertical.Count - 1;
        return BuildCellRectangles(pixels, vertical, horizontal, words).Select(members =>
            Rectangle.FromLTRB(vertical[members.Min(index => index % columns)].Position,
                horizontal[members.Min(index => index / columns)].Position,
                vertical[members.Max(index => index % columns) + 1].Position,
                horizontal[members.Max(index => index / columns) + 1].Position)).ToArray();
    }
}
