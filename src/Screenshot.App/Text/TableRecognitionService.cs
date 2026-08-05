using System.Drawing;

namespace Screenshot.App.Text;

public static class TableRecognitionService
{
    public static ContentRecognitionResult BuildTsv(
        OcrRecognitionResult ocr,
        Bitmap? sourceImage = null)
    {
        ArgumentNullException.ThrowIfNull(ocr);
        if (!ocr.IsSuccess)
        {
            return ContentRecognitionResult.Failure(
                "表格识别",
                ocr.ErrorMessage ?? "文字识别失败。");
        }

        var words = ocr.Words
            .Where(word => !string.IsNullOrWhiteSpace(word.Text))
            .OrderBy(word => word.Y + (word.Height / 2))
            .ThenBy(word => word.X)
            .ToArray();
        if (words.Length < 4)
        {
            return ContentRecognitionResult.Failure(
                "表格识别",
                "没有识别到足够的表格单元格，请完整框住表格后重试。");
        }

        var typicalHeight = Math.Max(
            6,
            words.Select(word => word.Height).Order().ElementAt(words.Length / 2));
        var rows = BuildRows(words, typicalHeight);
        if (rows.Count < 2)
        {
            return ContentRecognitionResult.Failure(
                "表格识别",
                "当前选区不像多行表格，请扩大选区后重试。");
        }

        // Windows OCR frequently splits one Chinese cell into several words.
        // Merge close fragments before inferring columns so each word does not
        // accidentally become an extra Excel column.
        var rowCells = rows
            .Select(row => MergeNearbyWords(row, typicalHeight))
            .ToArray();
        var candidateStarts = rowCells
            .SelectMany(row => row.Select(cell => cell.X))
            .Order()
            .ToArray();
        var clusters = ClusterPositions(
            candidateStarts,
            Math.Max(10, typicalHeight * 1.15));
        var minimumOccurrences = Math.Max(2, (int)Math.Ceiling(rows.Count * 0.6));
        var columns = clusters
            .Where(cluster => cluster.Count >= minimumOccurrences)
            .Select(cluster => cluster.Average())
            .Order()
            .ToArray();
        if (columns.Length < 2)
        {
            return ContentRecognitionResult.Failure(
                "表格识别",
                "未找到稳定的列边界。请只框选表格，并确保至少两行两列清晰可见。");
        }

        var completeRowCount = rowCells.Count(row => row.Count >= columns.Length);
        if (completeRowCount < Math.Max(2, (int)Math.Ceiling(rows.Count * 0.6)))
        {
            return ContentRecognitionResult.Failure(
                "表格识别",
                "当前内容的列结构不够稳定，未将普通多栏文字误判为表格。");
        }

        if (sourceImage is not null &&
            columns.Length < 3 &&
            !HasVisibleTableGrid(sourceImage, words))
        {
            return ContentRecognitionResult.Failure(
                "表格识别",
                "当前内容更像普通双栏文字，未显示表格结果。");
        }

        var lines = rows.Select(row => BuildLine(row, columns));
        return new ContentRecognitionResult(
            true,
            "表格识别",
            string.Join(Environment.NewLine, lines));
    }

    private static List<List<OcrWordRegion>> BuildRows(
        IReadOnlyList<OcrWordRegion> words,
        double typicalHeight)
    {
        var rows = new List<List<OcrWordRegion>>();
        foreach (var word in words)
        {
            var centerY = word.Y + (word.Height / 2);
            var row = rows.FirstOrDefault(candidate =>
            {
                var candidateCenter = candidate.Average(
                    item => item.Y + (item.Height / 2));
                return Math.Abs(candidateCenter - centerY) <= typicalHeight * 0.65;
            });
            if (row is null)
            {
                rows.Add([word]);
            }
            else
            {
                row.Add(word);
            }
        }

        return rows
            .OrderBy(row => row.Average(word => word.Y + (word.Height / 2)))
            .ToList();
    }

    private static List<CellFragment> MergeNearbyWords(
        IReadOnlyList<OcrWordRegion> row,
        double typicalHeight)
    {
        var ordered = row.OrderBy(word => word.X).ToArray();
        var cells = new List<CellFragment>();
        foreach (var word in ordered)
        {
            if (cells.Count == 0)
            {
                cells.Add(CellFragment.FromWord(word));
                continue;
            }

            var previous = cells[^1];
            var gap = word.X - previous.Right;
            var characterWidth = previous.EstimatedCharacterWidth;
            var mergeGap = Math.Max(
                typicalHeight * 0.75,
                Math.Min(typicalHeight * 1.35, characterWidth * 1.4));
            if (gap <= mergeGap)
            {
                cells[^1] = previous.Append(word, gap);
            }
            else
            {
                cells.Add(CellFragment.FromWord(word));
            }
        }

        return cells;
    }

    private static bool HasVisibleTableGrid(
        Bitmap bitmap,
        IReadOnlyList<OcrWordRegion> words)
    {
        if (bitmap.Width < 24 || bitmap.Height < 24)
        {
            return false;
        }

        var left = Math.Clamp((int)Math.Floor(words.Min(word => word.X)) - 8, 1, bitmap.Width - 2);
        var top = Math.Clamp((int)Math.Floor(words.Min(word => word.Y)) - 8, 1, bitmap.Height - 2);
        var right = Math.Clamp(
            (int)Math.Ceiling(words.Max(word => word.X + word.Width)) + 8,
            left + 1,
            bitmap.Width - 2);
        var bottom = Math.Clamp(
            (int)Math.Ceiling(words.Max(word => word.Y + word.Height)) + 8,
            top + 1,
            bitmap.Height - 2);

        var verticalLines = CountStraightEdges(
            bitmap,
            left,
            top,
            right,
            bottom,
            vertical: true);
        var horizontalLines = CountStraightEdges(
            bitmap,
            left,
            top,
            right,
            bottom,
            vertical: false);
        return verticalLines >= 3 && horizontalLines >= 3;
    }

    private static int CountStraightEdges(
        Bitmap bitmap,
        int left,
        int top,
        int right,
        int bottom,
        bool vertical)
    {
        var axisStart = vertical ? left : top;
        var axisEnd = vertical ? right : bottom;
        var sampleStart = vertical ? top : left;
        var sampleEnd = vertical ? bottom : right;
        var sampleCount = Math.Max(1, (sampleEnd - sampleStart + 1) / 2);
        var candidates = new List<int>();

        for (var axis = axisStart; axis <= axisEnd; axis++)
        {
            var edgeCount = 0;
            for (var sample = sampleStart; sample <= sampleEnd; sample += 2)
            {
                var center = vertical
                    ? bitmap.GetPixel(axis, sample)
                    : bitmap.GetPixel(sample, axis);
                var before = vertical
                    ? bitmap.GetPixel(axis - 1, sample)
                    : bitmap.GetPixel(sample, axis - 1);
                var after = vertical
                    ? bitmap.GetPixel(axis + 1, sample)
                    : bitmap.GetPixel(sample, axis + 1);
                if (ColorDistance(center, before) >= 24 ||
                    ColorDistance(center, after) >= 24)
                {
                    edgeCount++;
                }
            }

            if (edgeCount >= sampleCount * 0.42)
            {
                candidates.Add(axis);
            }
        }

        var lineCount = 0;
        var previous = int.MinValue;
        foreach (var candidate in candidates)
        {
            if (candidate - previous > 3)
            {
                lineCount++;
            }

            previous = candidate;
        }

        return lineCount;
    }

    private static int ColorDistance(Color first, Color second)
    {
        return Math.Abs(first.R - second.R) +
               Math.Abs(first.G - second.G) +
               Math.Abs(first.B - second.B);
    }

    private static List<List<double>> ClusterPositions(
        IEnumerable<double> positions,
        double tolerance)
    {
        var clusters = new List<List<double>>();
        foreach (var position in positions)
        {
            var cluster = clusters.FirstOrDefault(values =>
                Math.Abs(values.Average() - position) <= tolerance);
            if (cluster is null)
            {
                clusters.Add([position]);
            }
            else
            {
                cluster.Add(position);
            }
        }

        return clusters;
    }

    private static string BuildLine(
        IReadOnlyList<OcrWordRegion> row,
        IReadOnlyList<double> columns)
    {
        var cells = Enumerable.Range(0, columns.Count)
            .Select(_ => new List<OcrWordRegion>())
            .ToArray();
        foreach (var word in row.OrderBy(item => item.X))
        {
            var columnIndex = 0;
            for (var index = 1; index < columns.Count; index++)
            {
                // OCR engines can return one box per character. A long cell
                // naturally extends well past the midpoint between two column
                // starts, so midpoint assignment moves its trailing characters
                // into the next cell. Table text is left aligned: assign each
                // box to the latest detected column start that it reaches.
                var tolerance = Math.Max(3, word.Height * 0.2);
                if (word.X >= columns[index] - tolerance)
                {
                    columnIndex = index;
                }
            }

            cells[columnIndex].Add(word);
        }

        return string.Join('\t', cells.Select(cell =>
            EscapeSpreadsheetCell(JoinCellWords(cell))));
    }

    private static string JoinCellWords(IReadOnlyList<OcrWordRegion> words)
    {
        var ordered = words.OrderBy(word => word.X).ToArray();
        if (ordered.Length == 0)
        {
            return string.Empty;
        }

        var text = ordered[0].Text.Trim();
        for (var index = 1; index < ordered.Length; index++)
        {
            var previous = ordered[index - 1];
            var current = ordered[index];
            var separator = NeedsSpace(previous, current) ? " " : string.Empty;
            text += separator + current.Text.Trim();
        }

        return text;
    }

    private static bool NeedsSpace(
        OcrWordRegion left,
        OcrWordRegion right)
    {
        var leftText = left.Text.Trim();
        var rightText = right.Text.Trim();
        if (leftText.Length == 0 || rightText.Length == 0 ||
            IsJoiningPunctuation(leftText[^1]) ||
            IsJoiningPunctuation(rightText[0]))
        {
            return false;
        }

        var gap = right.X - (left.X + left.Width);
        var typicalCharacterWidth = Math.Min(
            left.Width / Math.Max(1, leftText.Length),
            right.Width / Math.Max(1, rightText.Length));
        if (gap > Math.Max(3, typicalCharacterWidth * 0.32))
        {
            return true;
        }

        if (gap >= 2 && IsCjkCharacter(leftText[^1]) !=
            IsCjkCharacter(rightText[0]))
        {
            return true;
        }

        // A pair of complete Latin OCR tokens with a visible gap represents
        // separate words. Single-character boxes such as P + C or i + O + S
        // must remain continuous.
        return gap > 0 &&
               (leftText.Length > 1 || rightText.Length > 1) &&
               NeedsSpace(leftText, rightText);
    }

    private static bool NeedsSpace(string left, string right)
    {
        return left.Length > 0 &&
               right.Length > 0 &&
               IsLatinWordCharacter(left[^1]) &&
               IsLatinWordCharacter(right[0]);
    }

    private static bool IsLatinWordCharacter(char value)
    {
        return char.IsAsciiLetterOrDigit(value);
    }

    private static bool IsJoiningPunctuation(char value)
    {
        return char.IsPunctuation(value) ||
               value is '/' or '\\' or '&';
    }

    private static bool IsCjkCharacter(char value)
    {
        return value is >= '\u3400' and <= '\u4dbf' or
            >= '\u4e00' and <= '\u9fff' or
            >= '\uf900' and <= '\ufaff';
    }

    private static string EscapeSpreadsheetCell(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length > 0 && trimmed[0] is '=' or '+' or '-' or '@'
            ? $"'{trimmed}"
            : trimmed;
    }

    private sealed record CellFragment(
        string Text,
        double X,
        double Right,
        double EstimatedCharacterWidth)
    {
        public static CellFragment FromWord(OcrWordRegion word)
        {
            var text = word.Text.Trim();
            return new CellFragment(
                text,
                word.X,
                word.X + word.Width,
                word.Width / Math.Max(1, text.Length));
        }

        public CellFragment Append(OcrWordRegion word, double gap)
        {
            var wordText = word.Text.Trim();
            var separator = NeedsSpace(Text, wordText) ? " " : string.Empty;
            var combined = Text + separator + wordText;
            var right = Math.Max(Right, word.X + word.Width);
            var contentWidth = Math.Max(1, right - X - Math.Max(0, gap));
            return this with
            {
                Text = combined,
                Right = right,
                EstimatedCharacterWidth =
                    contentWidth / Math.Max(1, combined.Replace(" ", string.Empty).Length),
            };
        }
    }
}
