using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;

namespace Screenshot.App.Text;

public static class TableRecognitionService
{
    public static ContentRecognitionResult BuildTsv(
        OcrRecognitionResult ocr,
        Bitmap? sourceImage = null,
        IReadOnlyList<OcrWordRegion>? supplementaryWords = null)
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
        if (sourceImage is not null &&
            TryBuildGridTable(
                sourceImage,
                words,
                supplementaryWords ?? [],
                out var gridResult))
        {
            return gridResult;
        }

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

        // A merged cell intentionally has fewer OCR fragments than a normal
        // row. Do not reject the whole table just because several rows do not
        // contain every column start; BuildLine will leave those cells empty
        // while retaining the merged cell's text in its first column.

        if (sourceImage is not null &&
            columns.Length < 3 &&
            !HasVisibleTableGrid(sourceImage, words))
        {
            return ContentRecognitionResult.Failure(
                "表格识别",
                "当前内容更像普通双栏文字，未显示表格结果。");
        }

        var matrix = rows
            .Select(row => BuildCells(row, columns))
            .ToArray();
        var lines = matrix.Select(row =>
            string.Join('\t', row.Select(EscapeSpreadsheetCell)));
        return new ContentRecognitionResult(
            true,
            "表格识别",
            string.Join(Environment.NewLine, lines))
        {
            ClipboardHtml = BuildHtml(matrix),
        };
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

    private static bool TryBuildGridTable(
        Bitmap bitmap,
        IReadOnlyList<OcrWordRegion> words,
        IReadOnlyList<OcrWordRegion> supplementaryWords,
        out ContentRecognitionResult result)
    {
        result = null!;
        if (bitmap.Width < 32 || bitmap.Height < 32)
        {
            return false;
        }

        using var pixels = BitmapPixelBuffer.Create(bitmap);
        var verticalLines = FindGridLines(pixels, vertical: true);
        var horizontalLines = FindGridLines(pixels, vertical: false);
        RemoveTextStrokeLines(verticalLines, horizontalLines, words);
        FilterGridIntersections(pixels, verticalLines, horizontalLines);
        AddInferredLeadingBoundary(verticalLines, words, vertical: true);
        AddInferredLeadingBoundary(horizontalLines, words, vertical: false);
        AddInferredRegularDetailBoundaries(
            pixels,
            verticalLines,
            horizontalLines,
            words);
        if (verticalLines.Count < 3 || horizontalLines.Count < 3)
        {
            return false;
        }

        var columnCount = verticalLines.Count - 1;
        var rowCount = horizontalLines.Count - 1;
        if (columnCount > 80 || rowCount > 200)
        {
            return false;
        }

        var unions = new CellUnion(rowCount * columnCount);
        var verticalSeparators = new bool[rowCount, columnCount + 1];
        var horizontalSeparators = new bool[rowCount + 1, columnCount];
        for (var row = 0; row < rowCount; row++)
        {
            for (var column = 1; column < columnCount; column++)
            {
                verticalSeparators[row, column] = HasVerticalSeparator(
                    pixels,
                    verticalLines[column].Position,
                    horizontalLines[row].Position,
                    horizontalLines[row + 1].Position,
                    words);
            }
        }
        for (var row = 1; row < rowCount; row++)
        {
            for (var column = 0; column < columnCount; column++)
            {
                horizontalSeparators[row, column] = HasHorizontalSeparator(
                    pixels,
                    horizontalLines[row].Position,
                    verticalLines[column].Position,
                    verticalLines[column + 1].Position,
                    words);
            }
        }

        for (var row = 0; row < rowCount; row++)
        {
            for (var column = 0; column < columnCount; column++)
            {
                var cellIndex = (row * columnCount) + column;
                if (column + 1 < columnCount &&
                    !verticalSeparators[row, column + 1])
                {
                    unions.Union(cellIndex, cellIndex + 1);
                }

                if (row + 1 < rowCount &&
                    !horizontalSeparators[row + 1, column])
                {
                    unions.Union(cellIndex, cellIndex + columnCount);
                }
            }
        }

        var components = Enumerable.Range(0, rowCount * columnCount)
            .GroupBy(unions.Find)
            .SelectMany(group => SplitIntoRectangles(
                group,
                rowCount,
                columnCount))
            .Select(rectangle => CreateGridCell(
                rectangle,
                columnCount,
                verticalLines,
                horizontalLines,
                words,
                supplementaryWords,
                pixels))
            .OrderBy(cell => cell.Row)
            .ThenBy(cell => cell.Column)
            .ToArray();
        if (components.Length < 4)
        {
            return false;
        }

        components = NormalizeStructuredGridCells(
            components,
            supplementaryWords,
            verticalLines,
            horizontalLines);

        var matrix = Enumerable.Range(0, rowCount)
            .Select(_ => new string[columnCount])
            .ToArray();
        foreach (var cell in components)
        {
            matrix[cell.Row][cell.Column] = cell.Text.ReplaceLineEndings(" ");
        }

        result = new ContentRecognitionResult(
            true,
            "表格识别",
            string.Join(
                Environment.NewLine,
                matrix.Select(row => string.Join(
                    '\t',
                    row.Select(value => EscapeSpreadsheetCell(value ?? string.Empty))))))
        {
            ClipboardHtml = BuildGridHtml(
                components,
                rowCount,
                columnCount,
                verticalLines,
                horizontalLines),
        };
        return true;
    }

    private static GridCell[] NormalizeStructuredGridCells(
        IReadOnlyList<GridCell> cells,
        IReadOnlyList<OcrWordRegion> supplementaryWords,
        IReadOnlyList<AxisLine> verticalLines,
        IReadOnlyList<AxisLine> horizontalLines)
    {
        var normalized = cells.ToArray();

        NormalizeScheduleColumns(normalized);

        foreach (var header in normalized.Where(cell =>
                     IsPercentageHeader(cell.Text)))
        {
            for (var index = 0; index < normalized.Length; index++)
            {
                var cell = normalized[index];
                if (cell.Row <= header.Row || !ColumnsOverlap(cell, header))
                {
                    continue;
                }

                var compact = CompactCellText(cell.Text);
                var digits = ExtractDigits(compact);
                if (digits.Length is >= 1 and <= 3 &&
                    compact.All(character =>
                        char.IsDigit(character) || character == '%'))
                {
                    normalized[index] = cell with { Text = $"{digits}%" };
                }
            }
        }

        foreach (var monthHeader in normalized.Where(cell =>
                     IsTimelineMonthHeader(cell.Text)))
        {
            var detailRow = monthHeader.Row + monthHeader.RowSpan;
            for (var index = 0; index < normalized.Length; index++)
            {
                var cell = normalized[index];
                if (cell.Row != detailRow || !ColumnsOverlap(cell, monthHeader))
                {
                    continue;
                }

                var left = verticalLines[cell.Column].Position;
                var top = horizontalLines[cell.Row].Position;
                var right = verticalLines[cell.Column + cell.ColumnSpan].Position;
                var bottom = horizontalLines[cell.Row + cell.RowSpan].Position;
                var compactCellText = CompactCellText(NormalizeDigits(cell.Text));
                var primaryDigits = compactCellText.Length > 0 &&
                                    compactCellText.All(char.IsDigit)
                    ? compactCellText
                    : null;
                var supplementaryDigits = supplementaryWords
                    .Where(word =>
                    {
                        var centerX = word.X + (word.Width / 2);
                        var centerY = word.Y + (word.Height / 2);
                        return centerX >= left && centerX <= right &&
                               centerY >= top && centerY <= bottom;
                    })
                    .Select(word => CompactCellText(NormalizeDigits(word.Text)))
                    .Where(value =>
                        value.Length > 0 && value.All(char.IsDigit))
                    .OrderByDescending(value => value.Length)
                    .FirstOrDefault();
                var digits = primaryDigits ??
                             supplementaryDigits ??
                             ExtractDigits(cell.Text);
                normalized[index] = cell with
                {
                    Text = digits.Length == 0
                        ? string.Empty
                        : string.Join(
                            Environment.NewLine,
                            digits.Select(character => character.ToString())),
                };
            }
        }

        return normalized;
    }

    private static void NormalizeScheduleColumns(GridCell[] cells)
    {
        var startHeader = cells.FirstOrDefault(cell =>
            CompactCellText(cell.Text) == "开始时间");
        var endHeader = cells.FirstOrDefault(cell =>
            CompactCellText(cell.Text) == "结束时间");
        if (startHeader is null || endHeader is null ||
            startHeader.Row != endHeader.Row ||
            startHeader.Column + startHeader.ColumnSpan != endHeader.Column)
        {
            return;
        }

        var statusColumn = endHeader.Column + endHeader.ColumnSpan;
        var percentageColumn = statusColumn + 1;
        for (var index = 0; index < cells.Length; index++)
        {
            var cell = cells[index];
            if (cell.Row == startHeader.Row && cell.Column == statusColumn)
            {
                cells[index] = cell with { Text = "状态" };
                continue;
            }
            if (cell.Row == startHeader.Row && cell.Column == percentageColumn)
            {
                cells[index] = cell with { Text = "完成比例" };
                continue;
            }
            if (cell.Row <= startHeader.Row)
            {
                continue;
            }

            if (ColumnsOverlap(cell, startHeader) ||
                ColumnsOverlap(cell, endHeader))
            {
                var date = NormalizeScheduleDate(cell.Text);
                if (date is not null)
                {
                    cells[index] = cell with { Text = date };
                }
                continue;
            }

            if (cell.Column <= statusColumn &&
                cell.Column + cell.ColumnSpan > statusColumn)
            {
                var status = NormalizeScheduleStatus(cell.Text);
                if (status is not null)
                {
                    cells[index] = cell with { Text = status };
                }
                continue;
            }

            if (cell.Column <= percentageColumn &&
                cell.Column + cell.ColumnSpan > percentageColumn)
            {
                var compact = CompactCellText(cell.Text);
                var digits = ExtractDigits(compact);
                if (digits.Length is >= 1 and <= 3 &&
                    compact.All(character =>
                        char.IsDigit(character) || character == '%'))
                {
                    cells[index] = cell with { Text = $"{digits}%" };
                }
            }
        }
    }

    private static string? NormalizeScheduleDate(string value)
    {
        var compact = CompactCellText(NormalizeDigits(value))
            .Replace("、", string.Empty, StringComparison.Ordinal)
            .Replace("，", string.Empty, StringComparison.Ordinal)
            .Replace(",", string.Empty, StringComparison.Ordinal)
            .Replace("．", string.Empty, StringComparison.Ordinal)
            .Replace(".", string.Empty, StringComparison.Ordinal);
        var monthIndex = compact.IndexOf('月');
        if (monthIndex is < 1 or > 2)
        {
            return null;
        }

        var dayIndex = compact.IndexOf('日', monthIndex + 1);
        var monthText = ExtractDigits(compact[..monthIndex]);
        var dayText = ExtractDigits(dayIndex > monthIndex
            ? compact[(monthIndex + 1)..dayIndex]
            : compact[(monthIndex + 1)..]);
        if (!int.TryParse(
                monthText,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var month) ||
            !int.TryParse(
                dayText,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var day) ||
            month is < 1 or > 12 || day is < 1 or > 31)
        {
            return null;
        }

        return $"{month}月{day}日";
    }

    private static string? NormalizeScheduleStatus(string value)
    {
        var compact = CompactCellText(value).Replace('0', 'O');
        if (!compact.StartsWith('O') ||
            !compact.All(character =>
                char.IsLetter(character) || character == '-') ||
            !compact.Contains("go", StringComparison.OrdinalIgnoreCase) &&
            !compact.Contains("in", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return "On-going";
    }

    private static bool ColumnsOverlap(GridCell first, GridCell second) =>
        first.Column < second.Column + second.ColumnSpan &&
        second.Column < first.Column + first.ColumnSpan;

    private static bool IsPercentageHeader(string value)
    {
        var compact = CompactCellText(value);
        return compact.Contains("完成比例", StringComparison.Ordinal) ||
               compact is "完成比率" or "比例";
    }

    private static bool IsTimelineMonthHeader(string value)
    {
        var compact = CompactCellText(NormalizeDigits(value));
        return compact.Contains('月') &&
               compact.Any(char.IsDigit) &&
               compact.All(character => char.IsDigit(character) || character == '月');
    }

    private static List<int[]> SplitIntoRectangles(
        IEnumerable<int> memberIndexes,
        int rowCount,
        int columnCount)
    {
        var remaining = memberIndexes.ToHashSet();
        var rectangles = new List<int[]>();
        while (remaining.Count > 0)
        {
            var start = remaining.Min();
            var startRow = start / columnCount;
            var startColumn = start % columnCount;
            var maximumWidth = 0;
            while (startColumn + maximumWidth < columnCount &&
                   remaining.Contains(start + maximumWidth))
            {
                maximumWidth++;
            }

            var bestWidth = 1;
            var bestHeight = 1;
            var bestArea = 1;
            for (var width = 1; width <= maximumWidth; width++)
            {
                var height = 0;
                while (startRow + height < rowCount &&
                       Enumerable.Range(0, width).All(offset =>
                           remaining.Contains(
                               ((startRow + height) * columnCount) +
                               startColumn + offset)))
                {
                    height++;
                }

                var area = width * height;
                if (area > bestArea ||
                    area == bestArea && width > bestWidth)
                {
                    bestArea = area;
                    bestWidth = width;
                    bestHeight = height;
                }
            }

            var rectangle = new List<int>(bestArea);
            for (var rowOffset = 0; rowOffset < bestHeight; rowOffset++)
            {
                for (var columnOffset = 0;
                     columnOffset < bestWidth;
                     columnOffset++)
                {
                    var index = ((startRow + rowOffset) * columnCount) +
                                startColumn + columnOffset;
                    if (remaining.Remove(index))
                    {
                        rectangle.Add(index);
                    }
                }
            }
            rectangles.Add(rectangle.ToArray());
        }

        return rectangles;
    }

    private static List<AxisLine> FindGridLines(
        BitmapPixelBuffer pixels,
        bool vertical)
    {
        const int edgeThreshold = 14;
        var axisLength = vertical ? pixels.Width : pixels.Height;
        var sampleLength = vertical ? pixels.Height : pixels.Width;
        var scores = new List<AxisLine>();
        var sampleCount = Math.Max(1, (sampleLength - 2) / 2);
        var minimumHits = Math.Max(8, (int)Math.Ceiling(sampleCount * 0.12));
        var minimumContinuousHits = Math.Max(
            6,
            (int)Math.Ceiling(sampleCount * 0.05));
        for (var axis = 1; axis < axisLength - 1; axis++)
        {
            var hits = 0;
            var continuousHits = 0;
            var longestContinuousRun = 0;
            var longestRunStart = 0;
            var longestRunEnd = 0;
            for (var sample = 1; sample < sampleLength - 1; sample += 2)
            {
                var strength = vertical
                    ? VerticalEdgeStrength(pixels, axis, sample)
                    : HorizontalEdgeStrength(pixels, sample, axis);
                if (strength >= edgeThreshold)
                {
                    hits++;
                    continuousHits++;
                    longestContinuousRun = Math.Max(
                        longestContinuousRun,
                        continuousHits);
                    if (continuousHits == longestContinuousRun)
                    {
                        longestRunStart = sample - ((continuousHits - 1) * 2);
                        longestRunEnd = sample;
                    }
                }
                else
                {
                    continuousHits = 0;
                }
            }

            if (hits >= minimumHits &&
                longestContinuousRun >= minimumContinuousHits)
            {
                scores.Add(new AxisLine(
                    axis,
                    hits,
                    longestRunStart,
                    longestRunEnd));
            }
        }

        AddBoundaryLineIfPresent(atEnd: false);
        AddBoundaryLineIfPresent(atEnd: true);

        void AddBoundaryLineIfPresent(bool atEnd)
        {
            var hits = 0;
            var continuousHits = 0;
            var longestContinuousRun = 0;
            var longestRunStart = 0;
            var longestRunEnd = 0;
            for (var sample = 1; sample < sampleLength - 1; sample += 2)
            {
                var strength = BoundaryEdgeStrength(
                    pixels,
                    vertical,
                    atEnd,
                    sample);
                if (strength >= edgeThreshold)
                {
                    hits++;
                    continuousHits++;
                    longestContinuousRun = Math.Max(
                        longestContinuousRun,
                        continuousHits);
                    if (continuousHits == longestContinuousRun)
                    {
                        longestRunStart = sample - ((continuousHits - 1) * 2);
                        longestRunEnd = sample;
                    }
                }
                else
                {
                    continuousHits = 0;
                }
            }

            if (hits >= minimumHits &&
                longestContinuousRun >= minimumContinuousHits)
            {
                scores.Add(new AxisLine(
                    atEnd ? axisLength - 1 : 0,
                    hits,
                    longestRunStart,
                    longestRunEnd));
            }
        }

        var clustered = new List<AxisLine>();
        foreach (var cluster in ClusterAxisLines(scores, 3))
        {
            clustered.Add(cluster.MaxBy(line => line.Score)!);
        }

        clustered.Sort((left, right) => left.Position.CompareTo(right.Position));
        for (var index = clustered.Count - 1; index > 0; index--)
        {
            if (clustered[index].Position - clustered[index - 1].Position >= 7)
            {
                continue;
            }

            if (clustered[index].Score > clustered[index - 1].Score)
            {
                clustered.RemoveAt(index - 1);
            }
            else
            {
                clustered.RemoveAt(index);
            }
        }

        return clustered;
    }

    private static void RemoveTextStrokeLines(
        List<AxisLine> verticalLines,
        List<AxisLine> horizontalLines,
        IReadOnlyList<OcrWordRegion> words)
    {
        if (words.Count == 0)
        {
            return;
        }

        verticalLines.RemoveAll(line => IsLongestRunInsideText(
            line,
            words
                .Where(word =>
                    line.Position >= word.X - 1 &&
                    line.Position <= word.X + word.Width + 1)
                .Select(word => (word.Y - 1, word.Y + word.Height + 1))));
        horizontalLines.RemoveAll(line => IsMostlyTextStroke(
            line,
            words
                .Where(word =>
                    line.Position >= word.Y - 1 &&
                    line.Position <= word.Y + word.Height + 1)
                .Select(word => (word.X - 1, word.X + word.Width + 1))));
    }

    private static bool IsMostlyTextStroke(
        AxisLine line,
        IEnumerable<(double Start, double End)> projectedRanges)
    {
        var ranges = projectedRanges
            .OrderBy(range => range.Start)
            .ToArray();
        if (ranges.Length == 0)
        {
            return false;
        }

        var coveredLength = 0d;
        var start = ranges[0].Start;
        var end = ranges[0].End;
        foreach (var range in ranges.Skip(1))
        {
            if (range.Start <= end)
            {
                end = Math.Max(end, range.End);
                continue;
            }

            coveredLength += end - start + 1;
            start = range.Start;
            end = range.End;
        }
        coveredLength += end - start + 1;

        var coveredSamples = Math.Max(1, (coveredLength + 1) / 2);
        return line.Score <= coveredSamples * 1.6;
    }

    private static bool IsLongestRunInsideText(
        AxisLine line,
        IEnumerable<(double Start, double End)> projectedRanges)
    {
        var runLength = line.RunEnd - line.RunStart + 1;
        if (runLength <= 0)
        {
            return false;
        }

        var ranges = projectedRanges
            .Select(range => (
                Start: Math.Max(range.Start, line.RunStart),
                End: Math.Min(range.End, line.RunEnd)))
            .Where(range => range.End >= range.Start)
            .OrderBy(range => range.Start)
            .ToArray();
        if (ranges.Length == 0)
        {
            return false;
        }

        var covered = 0d;
        var start = ranges[0].Start;
        var end = ranges[0].End;
        foreach (var range in ranges.Skip(1))
        {
            if (range.Start <= end)
            {
                end = Math.Max(end, range.End);
            }
            else
            {
                covered += end - start + 1;
                start = range.Start;
                end = range.End;
            }
        }
        covered += end - start + 1;
        return covered >= runLength * 0.65;
    }

    private static void AddInferredLeadingBoundary(
        List<AxisLine> lines,
        IReadOnlyList<OcrWordRegion> words,
        bool vertical)
    {
        if (lines.Count < 3 || lines[0].Position <= 3)
        {
            return;
        }

        var gaps = lines
            .Zip(lines.Skip(1), (left, right) => right.Position - left.Position)
            .Where(gap => gap >= 7)
            .Order()
            .ToArray();
        if (gaps.Length == 0)
        {
            return;
        }

        var typicalGap = gaps[gaps.Length / 2];
        var firstPosition = lines[0].Position;
        if (firstPosition > Math.Max(200, typicalGap * 2.5))
        {
            return;
        }

        var hasLeadingContent = words.Any(word =>
        {
            var center = vertical
                ? word.X + (word.Width / 2)
                : word.Y + (word.Height / 2);
            return center >= 0 && center < firstPosition - 2;
        });
        if (hasLeadingContent)
        {
            lines.Insert(0, new AxisLine(0, int.MaxValue, 0, 0));
        }
    }

    private static void AddInferredRegularDetailBoundaries(
        BitmapPixelBuffer pixels,
        List<AxisLine> verticalLines,
        IReadOnlyList<AxisLine> horizontalLines,
        IReadOnlyList<OcrWordRegion> words)
    {
        if (verticalLines.Count < 8 || horizontalLines.Count < 3)
        {
            return;
        }

        var narrowGaps = verticalLines
            .Zip(
                verticalLines.Skip(1),
                (left, right) => right.Position - left.Position)
            .Where(gap => gap is >= 18 and <= 44)
            .Order()
            .ToArray();
        if (narrowGaps.Length < 4)
        {
            return;
        }

        var typicalGap = narrowGaps[narrowGaps.Length / 2];
        var firstRegularGap = -1;
        for (var index = 0; index + 3 < verticalLines.Count - 1; index++)
        {
            var regularCount = 0;
            for (var offset = 0;
                 offset < 5 && index + offset < verticalLines.Count - 1;
                 offset++)
            {
                var gap = verticalLines[index + offset + 1].Position -
                          verticalLines[index + offset].Position;
                if (gap >= typicalGap * 0.55 && gap <= typicalGap * 1.3)
                {
                    regularCount++;
                }
            }

            if (regularCount >= 4)
            {
                firstRegularGap = index;
                break;
            }
        }
        if (firstRegularGap < 0)
        {
            return;
        }

        var inferred = new List<AxisLine>();
        for (var index = firstRegularGap;
             index < verticalLines.Count - 1;
             index++)
        {
            var left = verticalLines[index].Position;
            var right = verticalLines[index + 1].Position;
            var gap = right - left;
            var segmentCount = (int)Math.Round(gap / (double)typicalGap);
            if (segmentCount < 2 || segmentCount > 3 ||
                gap < typicalGap * 1.5 || gap > typicalGap * 3.35)
            {
                continue;
            }

            for (var segment = 1; segment < segmentCount; segment++)
            {
                var expected = left + ((gap * segment) / segmentCount);
                var searchRadius = Math.Max(4, typicalGap / 3);
                AxisLine? bestLine = null;
                double bestEvidence = 0;
                for (var x = Math.Max(left + 7, expected - searchRadius);
                     x <= Math.Min(right - 7, expected + searchRadius);
                     x++)
                {
                    var evidence = ScoreVerticalDetailSeparator(
                        pixels,
                        x,
                        horizontalLines,
                        words,
                        out var score,
                        out var runStart,
                        out var runEnd);
                    if (evidence <= bestEvidence)
                    {
                        continue;
                    }

                    bestEvidence = evidence;
                    bestLine = new AxisLine(x, score, runStart, runEnd);
                }

                if (bestLine is not null && bestEvidence >= 0.55)
                {
                    inferred.Add(bestLine);
                }
            }
        }

        verticalLines.AddRange(inferred);
        verticalLines.Sort((left, right) =>
            left.Position.CompareTo(right.Position));
    }

    private static double ScoreVerticalDetailSeparator(
        BitmapPixelBuffer pixels,
        int x,
        IReadOnlyList<AxisLine> horizontalLines,
        IReadOnlyList<OcrWordRegion> words,
        out int bestScore,
        out int bestRunStart,
        out int bestRunEnd)
    {
        var bestEvidence = 0d;
        bestScore = 0;
        bestRunStart = 0;
        bestRunEnd = 0;
        for (var row = 0; row + 1 < horizontalLines.Count; row++)
        {
            var top = horizontalLines[row].Position + 2;
            var bottom = horizontalLines[row + 1].Position - 2;
            var hits = 0;
            var samples = 0;
            var run = 0;
            var longestRun = 0;
            var longestRunEnd = top;
            for (var y = top; y <= bottom; y++)
            {
                if (words.Any(word =>
                    x >= word.X - 1 && x <= word.X + word.Width + 1 &&
                    y >= word.Y - 1 && y <= word.Y + word.Height + 1))
                {
                    run = 0;
                    continue;
                }

                samples++;
                if (VerticalEdgeStrength(pixels, x, y) >= 14)
                {
                    hits++;
                    run++;
                    if (run > longestRun)
                    {
                        longestRun = run;
                        longestRunEnd = y;
                    }
                }
                else
                {
                    run = 0;
                }
            }

            if (samples == 0)
            {
                continue;
            }

            var intervalHeight = Math.Max(1, bottom - top + 1);
            var evidence = (hits / (double)samples) * 0.4 +
                           (longestRun / (double)intervalHeight) * 0.6;
            if (evidence <= bestEvidence)
            {
                continue;
            }

            bestEvidence = evidence;
            bestScore = hits;
            bestRunStart = longestRunEnd - longestRun + 1;
            bestRunEnd = longestRunEnd;
        }

        return bestEvidence;
    }

    private static IEnumerable<List<AxisLine>> ClusterAxisLines(
        IReadOnlyList<AxisLine> lines,
        int maximumGap)
    {
        var cluster = new List<AxisLine>();
        foreach (var line in lines.OrderBy(line => line.Position))
        {
            if (cluster.Count > 0 &&
                line.Position - cluster[^1].Position > maximumGap)
            {
                yield return cluster;
                cluster = [];
            }
            cluster.Add(line);
        }

        if (cluster.Count > 0)
        {
            yield return cluster;
        }
    }

    private static void FilterGridIntersections(
        BitmapPixelBuffer pixels,
        List<AxisLine> verticalLines,
        List<AxisLine> horizontalLines)
    {
        if (verticalLines.Count < 3 || horizontalLines.Count < 3)
        {
            return;
        }

        verticalLines.RemoveAll(vertical =>
            horizontalLines.Count(horizontal => IsGridIntersection(
                pixels,
                vertical.Position,
                horizontal.Position)) < 2);
        horizontalLines.RemoveAll(horizontal =>
            verticalLines.Count(vertical => IsGridIntersection(
                pixels,
                vertical.Position,
                horizontal.Position)) < 2);
    }

    private static bool IsGridIntersection(
        BitmapPixelBuffer pixels,
        int x,
        int y)
    {
        var vertical = 0;
        var horizontal = 0;
        for (var offset = -2; offset <= 2; offset++)
        {
            vertical = Math.Max(
                vertical,
                VerticalEdgeStrength(pixels, x, y + offset));
            horizontal = Math.Max(
                horizontal,
                HorizontalEdgeStrength(pixels, x + offset, y));
        }
        return vertical >= 14 && horizontal >= 14;
    }

    private static bool HasVerticalSeparator(
        BitmapPixelBuffer pixels,
        int x,
        int top,
        int bottom,
        IReadOnlyList<OcrWordRegion> words)
    {
        var start = Math.Clamp(top + 2, 1, pixels.Height - 2);
        var end = Math.Clamp(bottom - 2, start, pixels.Height - 2);
        var samples = 0;
        var hits = 0;
        for (var y = start; y <= end; y++)
        {
            if (words.Any(word =>
                x >= word.X - 1 && x <= word.X + word.Width + 1 &&
                y >= word.Y - 1 && y <= word.Y + word.Height + 1))
            {
                continue;
            }
            samples++;
            if (VerticalEdgeStrength(pixels, x, y) >= 14)
            {
                hits++;
            }
        }
        return samples > 0 && hits >= Math.Max(3, samples * 0.28);
    }

    private static bool HasHorizontalSeparator(
        BitmapPixelBuffer pixels,
        int y,
        int left,
        int right,
        IReadOnlyList<OcrWordRegion> words)
    {
        var start = Math.Clamp(left + 2, 1, pixels.Width - 2);
        var end = Math.Clamp(right - 2, start, pixels.Width - 2);
        var samples = 0;
        var hits = 0;
        for (var x = start; x <= end; x++)
        {
            if (words.Any(word =>
                x >= word.X - 1 && x <= word.X + word.Width + 1 &&
                y >= word.Y - 1 && y <= word.Y + word.Height + 1))
            {
                continue;
            }
            samples++;
            if (HorizontalEdgeStrength(pixels, x, y) >= 14)
            {
                hits++;
            }
        }
        return samples > 0 && hits >= Math.Max(3, samples * 0.28);
    }

    private static int VerticalEdgeStrength(BitmapPixelBuffer pixels, int x, int y)
    {
        x = Math.Clamp(x, 1, pixels.Width - 2);
        y = Math.Clamp(y, 0, pixels.Height - 1);
        var center = pixels.GetColor(x, y);
        return Math.Max(
            ColorDistance(center, pixels.GetColor(x - 1, y)),
            ColorDistance(center, pixels.GetColor(x + 1, y)));
    }

    private static int HorizontalEdgeStrength(BitmapPixelBuffer pixels, int x, int y)
    {
        x = Math.Clamp(x, 0, pixels.Width - 1);
        y = Math.Clamp(y, 1, pixels.Height - 2);
        var center = pixels.GetColor(x, y);
        return Math.Max(
            ColorDistance(center, pixels.GetColor(x, y - 1)),
            ColorDistance(center, pixels.GetColor(x, y + 1)));
    }

    private static int BoundaryEdgeStrength(
        BitmapPixelBuffer pixels,
        bool vertical,
        bool atEnd,
        int sample)
    {
        if (vertical)
        {
            var boundary = atEnd ? pixels.Width - 1 : 0;
            var boundaryColor = pixels.GetColor(boundary, sample);
            var strength = 0;
            for (var offset = 1; offset <= Math.Min(4, pixels.Width - 1); offset++)
            {
                var neighbor = atEnd ? boundary - offset : offset;
                strength = Math.Max(
                    strength,
                    ColorDistance(
                        boundaryColor,
                        pixels.GetColor(neighbor, sample)));
            }
            return strength;
        }

        var horizontalBoundary = atEnd ? pixels.Height - 1 : 0;
        var horizontalBoundaryColor = pixels.GetColor(
            sample,
            horizontalBoundary);
        var horizontalStrength = 0;
        for (var offset = 1;
             offset <= Math.Min(4, pixels.Height - 1);
             offset++)
        {
            var horizontalNeighbor = atEnd
                ? horizontalBoundary - offset
                : offset;
            horizontalStrength = Math.Max(
                horizontalStrength,
                ColorDistance(
                    horizontalBoundaryColor,
                    pixels.GetColor(sample, horizontalNeighbor)));
        }
        return horizontalStrength;
    }

    private static GridCell CreateGridCell(
        IReadOnlyList<int> memberIndexes,
        int columnCount,
        IReadOnlyList<AxisLine> verticalLines,
        IReadOnlyList<AxisLine> horizontalLines,
        IReadOnlyList<OcrWordRegion> words,
        IReadOnlyList<OcrWordRegion> supplementaryWords,
        BitmapPixelBuffer pixels)
    {
        var rows = memberIndexes.Select(index => index / columnCount).ToArray();
        var columns = memberIndexes.Select(index => index % columnCount).ToArray();
        var row = rows.Min();
        var column = columns.Min();
        var rowSpan = rows.Max() - row + 1;
        var columnSpan = columns.Max() - column + 1;
        var left = verticalLines[column].Position;
        var top = horizontalLines[row].Position;
        var right = verticalLines[column + columnSpan].Position;
        var bottom = horizontalLines[row + rowSpan].Position;
        var cellWords = words.Where(word =>
        {
            var centerX = word.X + (word.Width / 2);
            var centerY = word.Y + (word.Height / 2);
            return centerX >= left && centerX <= right &&
                   centerY >= top && centerY <= bottom;
        }).ToArray();
        var supplementaryCellWords = supplementaryWords.Where(word =>
        {
            var centerX = word.X + (word.Width / 2);
            var centerY = word.Y + (word.Height / 2);
            return centerX >= left && centerX <= right &&
                   centerY >= top && centerY <= bottom;
        }).ToArray();
        var text = ResolveCellText(cellWords, supplementaryCellWords);
        text = NormalizeNarrowCellText(
            text,
            right - left,
            bottom - top);
        var background = SampleBackgroundColor(
            pixels,
            left,
            top,
            right,
            bottom,
            cellWords);
        var horizontalAlignment = ResolveHorizontalAlignment(
            cellWords,
            left,
            right);
        return new GridCell(
            row,
            column,
            rowSpan,
            columnSpan,
            text,
            background,
            horizontalAlignment);
    }

    private static string JoinCellLines(IReadOnlyList<OcrWordRegion> words)
    {
        if (words.Count == 0)
        {
            return string.Empty;
        }

        var typicalHeight = Math.Max(
            4,
            words.Select(word => word.Height).Order().ElementAt(words.Count / 2));
        return string.Join(
            Environment.NewLine,
            BuildRows(words, typicalHeight)
                .Select(JoinCellWords)
                .Where(line => !string.IsNullOrWhiteSpace(line)));
    }

    private static string ResolveCellText(
        IReadOnlyList<OcrWordRegion> primaryWords,
        IReadOnlyList<OcrWordRegion> supplementaryWords)
    {
        var primary = JoinCellLines(primaryWords);
        if (supplementaryWords.Count == 0)
        {
            return primary;
        }

        var supplementary = JoinCellLines(supplementaryWords);
        var primaryCompact = CompactCellText(primary);
        var supplementaryCompact = CompactCellText(supplementary);

        if (IsCompleteStructuredValue(primaryCompact))
        {
            return NormalizeDigits(primaryCompact);
        }

        var completeStructuredWord = supplementaryWords
            .Select(word => CompactCellText(word.Text))
            .FirstOrDefault(IsCompleteStructuredValue);
        if (completeStructuredWord is not null)
        {
            return NormalizeDigits(completeStructuredWord);
        }

        var date = ResolveDateText(primaryCompact, supplementaryCompact);
        if (date is not null)
        {
            return date;
        }

        if (primaryCompact.Contains('%') || supplementaryCompact.Contains('%'))
        {
            var digits = ExtractDigits(supplementaryCompact);
            if (digits.Length == 0)
            {
                digits = ExtractDigits(primaryCompact);
            }
            if (digits.Length > 0)
            {
                return $"{digits}%";
            }
        }

        if (IsCleanNumericText(primaryCompact))
        {
            return primary;
        }

        if (supplementaryCompact.Contains('月') &&
            ExtractDigits(supplementaryCompact).Length > 0)
        {
            return NormalizeDigits(supplementary
                .Replace(" ", string.Empty, StringComparison.Ordinal)
                .Replace("\t", string.Empty, StringComparison.Ordinal));
        }

        var supplementaryDigits = ExtractDigits(supplementaryCompact);
        var verticalNumericWord = supplementaryWords
            .Where(word => word.Height >= word.Width * 1.25)
            .Select(word => NormalizeDigits(CompactCellText(word.Text)))
            .Where(value => value.Length > 0 && value.All(char.IsDigit))
            .OrderByDescending(value => value.Length)
            .FirstOrDefault();
        if (verticalNumericWord is not null &&
            IsNumericLikePrimary(primaryCompact))
        {
            return string.Join(
                Environment.NewLine,
                verticalNumericWord.Select(character => character.ToString()));
        }

        if (supplementaryDigits.Length > 0 &&
            supplementaryCompact.All(character =>
                char.IsDigit(character) || char.IsWhiteSpace(character)) &&
            IsNumericLikePrimary(primaryCompact))
        {
            return supplementary;
        }

        return primary;
    }

    private static string NormalizeNarrowCellText(
        string text,
        int width,
        int height)
    {
        if (string.IsNullOrWhiteSpace(text) ||
            height < width * 1.2 ||
            !text.Any(char.IsWhiteSpace))
        {
            return text;
        }

        var compact = CompactCellText(text);
        return compact.Length > 0 && compact.All(char.IsDigit)
            ? text
            : compact;
    }

    private static bool IsCleanNumericText(string value) =>
        value.Length > 0 && value.All(char.IsDigit);

    private static bool IsNumericLikePrimary(string value) =>
        value.Length == 0 ||
        value.Any(char.IsDigit) &&
        value.All(character =>
            char.IsDigit(character) || "Il|!OoSGBZ".Contains(character));

    private static bool IsCompleteStructuredValue(string value)
    {
        if (value.EndsWith('%') &&
            value[..^1].Length is >= 1 and <= 3 &&
            value[..^1].All(char.IsDigit))
        {
            return true;
        }

        var normalized = NormalizeDigits(value);
        var monthIndex = normalized.IndexOf('月');
        var dayIndex = normalized.IndexOf('日', monthIndex + 1);
        if (monthIndex is < 1 or > 2 ||
            dayIndex <= monthIndex + 1 ||
            !normalized[..monthIndex].All(char.IsDigit) ||
            normalized[(monthIndex + 1)..dayIndex].Length is < 1 or > 2 ||
            !normalized[(monthIndex + 1)..dayIndex].All(char.IsDigit))
        {
            return false;
        }

        return int.TryParse(
                   normalized[..monthIndex],
                   NumberStyles.None,
                   CultureInfo.InvariantCulture,
                   out var month) &&
               int.TryParse(
                   normalized[(monthIndex + 1)..dayIndex],
                   NumberStyles.None,
                   CultureInfo.InvariantCulture,
                   out var day) &&
               month is >= 1 and <= 12 &&
               day is >= 1 and <= 31;
    }

    private static string? ResolveDateText(
        string primary,
        string supplementary)
    {
        if (!primary.Contains('月') && !supplementary.Contains('月') ||
            !primary.Contains('日') && !supplementary.Contains('日'))
        {
            return null;
        }

        foreach (var candidate in new[] { supplementary })
        {
            var monthIndex = candidate.IndexOf('月');
            var dayIndex = candidate.IndexOf('日', monthIndex + 1);
            if (monthIndex <= 0 || dayIndex <= monthIndex)
            {
                continue;
            }

            var month = ExtractDigits(candidate[..monthIndex]);
            var day = ExtractDigits(candidate[(monthIndex + 1)..dayIndex]);
            if (month.Length is >= 1 and <= 2 &&
                day.Length is >= 1 and <= 2)
            {
                return $"{month}月{day}日";
            }
        }

        var primaryMonthForMerge = primary.IndexOf('月');
        var primaryDayForMerge = primary.IndexOf('日', primaryMonthForMerge + 1);
        if (primaryMonthForMerge > 0 && primaryDayForMerge > primaryMonthForMerge)
        {
            var month = ExtractDigits(primary[..primaryMonthForMerge]);
            var supplementaryMonthIndex = supplementary.IndexOf('月');
            var day = supplementaryMonthIndex >= 0
                ? ExtractDigits(supplementary[(supplementaryMonthIndex + 1)..])
                : ExtractDigits(supplementary);
            if (supplementaryMonthIndex > 0)
            {
                var supplementaryMonth = ExtractDigits(
                    supplementary[..supplementaryMonthIndex]);
                if (supplementaryMonth.Length is >= 1 and <= 2)
                {
                    month = supplementaryMonth;
                }
            }
            if (month.Length is >= 1 and <= 2 &&
                day.Length is >= 1 and <= 2)
            {
                return $"{month}月{day}日";
            }
        }

        var primaryMonthIndex = primary.IndexOf('月');
        var primaryDayIndex = primary.IndexOf('日', primaryMonthIndex + 1);
        if (primaryMonthIndex > 0 && primaryDayIndex > primaryMonthIndex)
        {
            var month = ExtractDigits(primary[..primaryMonthIndex]);
            var day = ExtractDigits(
                primary[(primaryMonthIndex + 1)..primaryDayIndex]);
            if (month.Length is >= 1 and <= 2 &&
                day.Length is >= 1 and <= 2)
            {
                return $"{month}月{day}日";
            }
        }

        return null;
    }

    internal static (
        IReadOnlyList<int> Vertical,
        IReadOnlyList<int> Horizontal) FindGridLinePositions(
            Bitmap bitmap,
            IReadOnlyList<OcrWordRegion> words)
    {
        using var pixels = BitmapPixelBuffer.Create(bitmap);
        var verticalLines = FindGridLines(pixels, vertical: true);
        var horizontalLines = FindGridLines(pixels, vertical: false);
        RemoveTextStrokeLines(verticalLines, horizontalLines, words);
        FilterGridIntersections(pixels, verticalLines, horizontalLines);
        AddInferredLeadingBoundary(verticalLines, words, vertical: true);
        AddInferredLeadingBoundary(horizontalLines, words, vertical: false);
        AddInferredRegularDetailBoundaries(
            pixels,
            verticalLines,
            horizontalLines,
            words);
        return (
            verticalLines.Select(line => line.Position).ToArray(),
            horizontalLines.Select(line => line.Position).ToArray());
    }

    private static string CompactCellText(string value) =>
        string.Concat(value.Where(character => !char.IsWhiteSpace(character)));

    private static string ExtractDigits(string value) =>
        NormalizeDigits(string.Concat(value.Where(char.IsDigit)));

    private static string NormalizeDigits(string value) =>
        string.Concat(value.Select(character =>
        {
            if (character is >= '0' and <= '9')
            {
                return character;
            }

            var numericValue = char.GetNumericValue(character);
            return numericValue is >= 0 and <= 9 &&
                   Math.Abs(numericValue - Math.Round(numericValue)) < 0.01
                ? (char)('0' + (int)numericValue)
                : character;
        }));

    private static Color SampleBackgroundColor(
        BitmapPixelBuffer pixels,
        int left,
        int top,
        int right,
        int bottom,
        IReadOnlyList<OcrWordRegion> words)
    {
        var colors = new Dictionary<int, int>();
        var step = Math.Max(1, Math.Min(right - left, bottom - top) / 45);
        for (var y = top + 3; y < bottom - 2; y += step)
        {
            for (var x = left + 3; x < right - 2; x += step)
            {
                if (words.Any(word =>
                    x >= word.X - 2 && x <= word.X + word.Width + 2 &&
                    y >= word.Y - 2 && y <= word.Y + word.Height + 2))
                {
                    continue;
                }

                var color = pixels.GetColor(x, y);
                var red = Math.Min(255, ((color.R + 4) / 8) * 8);
                var green = Math.Min(255, ((color.G + 4) / 8) * 8);
                var blue = Math.Min(255, ((color.B + 4) / 8) * 8);
                var key = (red << 16) | (green << 8) | blue;
                colors[key] = colors.GetValueOrDefault(key) + 1;
            }
        }

        if (colors.Count == 0)
        {
            return Color.White;
        }

        var selected = colors.MaxBy(pair => pair.Value).Key;
        return Color.FromArgb(
            (selected >> 16) & 0xff,
            (selected >> 8) & 0xff,
            selected & 0xff);
    }

    private static string ResolveHorizontalAlignment(
        IReadOnlyList<OcrWordRegion> words,
        int left,
        int right)
    {
        if (words.Count == 0)
        {
            return "left";
        }

        var contentLeft = words.Min(word => word.X);
        var contentRight = words.Max(word => word.X + word.Width);
        var contentCenter = (contentLeft + contentRight) / 2;
        var cellCenter = (left + right) / 2d;
        var tolerance = Math.Max(5, (right - left) * 0.12);
        if (Math.Abs(contentCenter - cellCenter) <= tolerance)
        {
            return "center";
        }

        return right - contentRight < contentLeft - left ? "right" : "left";
    }

    private static string BuildGridHtml(
        IReadOnlyList<GridCell> cells,
        int rowCount,
        int columnCount,
        IReadOnlyList<AxisLine> verticalLines,
        IReadOnlyList<AxisLine> horizontalLines)
    {
        var lookup = cells.ToDictionary(cell => (cell.Row, cell.Column));
        var covered = new bool[rowCount, columnCount];
        var builder = new StringBuilder(
            "<table style=\"border-collapse:collapse;font-family:'Microsoft YaHei UI';font-size:11pt\">");
        for (var row = 0; row < rowCount; row++)
        {
            var rowHeight = Math.Max(
                1,
                horizontalLines[row + 1].Position - horizontalLines[row].Position);
            builder.Append("<tr style=\"height:")
                .Append(rowHeight)
                .Append("px\">");
            for (var column = 0; column < columnCount; column++)
            {
                if (covered[row, column] ||
                    !lookup.TryGetValue((row, column), out var cell))
                {
                    continue;
                }

                for (var coveredRow = row;
                     coveredRow < Math.Min(rowCount, row + cell.RowSpan);
                     coveredRow++)
                {
                    for (var coveredColumn = column;
                         coveredColumn < Math.Min(columnCount, column + cell.ColumnSpan);
                         coveredColumn++)
                    {
                        covered[coveredRow, coveredColumn] = true;
                    }
                }

                var width = verticalLines[column + cell.ColumnSpan].Position -
                            verticalLines[column].Position;
                var luminance = (cell.Background.R * 299 +
                                 cell.Background.G * 587 +
                                 cell.Background.B * 114) / 1000;
                var foreground = luminance < 135 ? "#FFFFFF" : "#20272B";
                builder.Append("<td style=\"border:1px solid #B7C0C7;width:")
                    .Append(Math.Max(1, width))
                    .Append("px;background-color:#")
                    .Append(cell.Background.R.ToString("X2", CultureInfo.InvariantCulture))
                    .Append(cell.Background.G.ToString("X2", CultureInfo.InvariantCulture))
                    .Append(cell.Background.B.ToString("X2", CultureInfo.InvariantCulture))
                    .Append(";color:")
                    .Append(foreground)
                    .Append(";text-align:")
                    .Append(cell.HorizontalAlignment)
                    .Append(";vertical-align:middle;white-space:nowrap;word-break:keep-all;mso-number-format:'\\@';padding:4px\"");
                if (cell.ColumnSpan > 1)
                {
                    builder.Append(" colspan=\"").Append(cell.ColumnSpan).Append('"');
                }
                if (cell.RowSpan > 1)
                {
                    builder.Append(" rowspan=\"").Append(cell.RowSpan).Append('"');
                }

                builder.Append('>');
                var lines = cell.Text.ReplaceLineEndings("\n").Split('\n');
                for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
                {
                    if (lineIndex > 0)
                    {
                        builder.Append("<br>");
                    }
                    builder.Append(WebUtility.HtmlEncode(lines[lineIndex]));
                }
                builder.Append("</td>");
            }
            builder.Append("</tr>");
        }

        return builder.Append("</table>").ToString();
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

    private static string[] BuildCells(
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

        return cells.Select(JoinCellWords).ToArray();
    }

    private static string BuildHtml(IReadOnlyList<string[]> rows)
    {
        var occupied = rows
            .Select(row => new bool[row.Length])
            .ToArray();
        var builder = new System.Text.StringBuilder(
            "<table border=\"1\" cellspacing=\"0\" cellpadding=\"4\">");
        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            var row = rows[rowIndex];
            builder.Append("<tr>");
            for (var index = 0; index < row.Length; index++)
            {
                var value = row[index];
                if (occupied[rowIndex][index] || string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                var span = 1;
                while (index + span < row.Length &&
                       string.IsNullOrWhiteSpace(row[index + span]))
                {
                    span++;
                }

                var rowSpan = 1;
                while (rowIndex + rowSpan < rows.Count &&
                       Enumerable.Range(index, span).All(column =>
                           column < rows[rowIndex + rowSpan].Length &&
                           string.IsNullOrWhiteSpace(rows[rowIndex + rowSpan][column])))
                {
                    rowSpan++;
                }

                for (var markedRow = rowIndex;
                     markedRow < rowIndex + rowSpan;
                     markedRow++)
                {
                    for (var markedColumn = index;
                         markedColumn < index + span &&
                         markedColumn < occupied[markedRow].Length;
                         markedColumn++)
                    {
                        occupied[markedRow][markedColumn] = true;
                    }
                }

                builder.Append("<td");
                if (span > 1)
                {
                    builder.Append(" colspan=\"").Append(span).Append('\"');
                }
                if (rowSpan > 1)
                {
                    builder.Append(" rowspan=\"").Append(rowSpan).Append('\"');
                }

                builder.Append('>')
                    .Append(System.Net.WebUtility.HtmlEncode(value))
                    .Append("</td>");
                index += span - 1;
            }

            builder.Append("</tr>");
        }

        return builder.Append("</table>").ToString();
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

    private sealed record AxisLine(
        int Position,
        int Score,
        int RunStart,
        int RunEnd);

    private sealed record GridCell(
        int Row,
        int Column,
        int RowSpan,
        int ColumnSpan,
        string Text,
        Color Background,
        string HorizontalAlignment);

    private sealed class CellUnion
    {
        private readonly int[] _parents;

        public CellUnion(int count)
        {
            _parents = Enumerable.Range(0, count).ToArray();
        }

        public int Find(int value)
        {
            while (_parents[value] != value)
            {
                _parents[value] = _parents[_parents[value]];
                value = _parents[value];
            }
            return value;
        }

        public void Union(int first, int second)
        {
            var firstRoot = Find(first);
            var secondRoot = Find(second);
            if (firstRoot != secondRoot)
            {
                _parents[secondRoot] = firstRoot;
            }
        }
    }

    private sealed class BitmapPixelBuffer : IDisposable
    {
        private readonly Bitmap _bitmap;
        private readonly byte[] _pixels;
        private readonly int _stride;

        private BitmapPixelBuffer(Bitmap bitmap, byte[] pixels, int stride)
        {
            _bitmap = bitmap;
            _pixels = pixels;
            _stride = stride;
        }

        public int Width => _bitmap.Width;

        public int Height => _bitmap.Height;

        public static BitmapPixelBuffer Create(Bitmap source)
        {
            var bitmap = source.Clone(
                new Rectangle(0, 0, source.Width, source.Height),
                PixelFormat.Format32bppArgb);
            var data = bitmap.LockBits(
                new Rectangle(0, 0, bitmap.Width, bitmap.Height),
                ImageLockMode.ReadOnly,
                PixelFormat.Format32bppArgb);
            try
            {
                var byteCount = Math.Abs(data.Stride) * bitmap.Height;
                var pixels = new byte[byteCount];
                Marshal.Copy(data.Scan0, pixels, 0, byteCount);
                return new BitmapPixelBuffer(bitmap, pixels, data.Stride);
            }
            catch
            {
                bitmap.Dispose();
                throw;
            }
            finally
            {
                bitmap.UnlockBits(data);
            }
        }

        public Color GetColor(int x, int y)
        {
            x = Math.Clamp(x, 0, Width - 1);
            y = Math.Clamp(y, 0, Height - 1);
            var row = _stride >= 0 ? y : Height - 1 - y;
            var offset = (row * Math.Abs(_stride)) + (x * 4);
            return Color.FromArgb(
                _pixels[offset + 3],
                _pixels[offset + 2],
                _pixels[offset + 1],
                _pixels[offset]);
        }

        public void Dispose()
        {
            _bitmap.Dispose();
        }
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
