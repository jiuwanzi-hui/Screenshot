using Screenshot.App.Capture;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;

namespace Screenshot.App.Text;

public static class TableSupplementaryOcrService
{
    public static async Task<IReadOnlyList<OcrWordRegion>> RecognizeAsync(
        CapturedImage image,
        IReadOnlyList<OcrWordRegion>? primaryWords = null,
        HighQualityOcrModelManager? modelManager = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        var languages = OcrService.GetAvailableLanguageTags();
        var language = languages.FirstOrDefault(tag => tag.StartsWith(
            "zh-Hans",
            StringComparison.OrdinalIgnoreCase)) ??
            languages.FirstOrDefault(tag => tag.StartsWith(
                "zh",
                StringComparison.OrdinalIgnoreCase)) ??
            (languages.Count > 0 ? languages[0] : null);
        if (language is null)
        {
            return [];
        }

        var recognition = await OcrService.RecognizeAsync(
            image,
            language,
            cancellationToken);
        var words = recognition.IsSuccess
            ? recognition.Words.ToList()
            : [];

        if (primaryWords is { Count: > 0 })
        {
            var gridLines = TableRecognitionService.FindGridLinePositions(
                image.Bitmap,
                primaryWords);
            using var atlasImage = BuildRotatedCellAtlas(
                image.Bitmap,
                primaryWords,
                gridLines.Vertical,
                gridLines.Horizontal,
                out var atlasCells);
            if (atlasImage is not null)
            {
                var atlasRecognition = await HighQualityOcrService.RecognizeAsync(
                    atlasImage,
                    modelManager,
                    cancellationToken);
                if (!atlasRecognition.IsSuccess)
                {
                    atlasRecognition = await OcrService.RecognizeAsync(
                        atlasImage,
                        language,
                        cancellationToken);
                }
                if (atlasRecognition.IsSuccess)
                {
                    foreach (var cell in atlasCells)
                    {
                        var text = cell.DirectText ??
                            string.Concat(atlasRecognition.Words
                                .Where(word => ContainsCenter(
                                    cell.Destination,
                                    word))
                                .OrderBy(word => word.X)
                                .ThenBy(word => word.Y)
                                .Select(word => word.Text));
                        var acceptedText = NormalizeAtlasText(text);
                        if (acceptedText is not null)
                        {
                            words.Add(new OcrWordRegion(
                                acceptedText,
                                cell.Source.X + 1,
                                cell.Source.Y + 1,
                                Math.Max(1, cell.Source.Width - 2),
                                Math.Max(1, cell.Source.Height - 2)));
                        }
                    }
                }
            }
        }

        return words;
    }

    private static CapturedImage? BuildRotatedCellAtlas(
        Bitmap source,
        IReadOnlyList<OcrWordRegion> primaryWords,
        IReadOnlyList<int> verticalLines,
        IReadOnlyList<int> horizontalLines,
        out IReadOnlyList<AtlasCell> atlasCells)
    {
        var sourceCells = new List<(
            Rectangle Source,
            Bitmap Content,
            string? DirectText)>();
        // The first grid row contains merged month/header cells. Subdividing it
        // with detail-column boundaries can crop a header into misleading
        // numeric fragments (for example, "10月" becoming an extra "1").
        for (var row = 1; row + 1 < horizontalLines.Count; row++)
        {
            for (var column = 0; column + 1 < verticalLines.Count; column++)
            {
                var rectangle = Rectangle.FromLTRB(
                    verticalLines[column] + 2,
                    horizontalLines[row] + 2,
                    verticalLines[column + 1] - 2,
                    horizontalLines[row + 1] - 2);
                if (rectangle.Width < 8 || rectangle.Height < 16 ||
                    rectangle.Height < rectangle.Width * 1.2)
                {
                    continue;
                }

                var content = BuildHorizontalCell(
                    source,
                    rectangle,
                    out var glyphs);
                if (content is not null)
                {
                    var directText = row == 1
                        ? RecognizeStackedDigits(
                            source,
                            rectangle,
                            glyphs,
                            primaryWords)
                        : null;
                    sourceCells.Add((rectangle, content, directText));
                }
            }
        }

        if (sourceCells.Count == 0)
        {
            atlasCells = [];
            return null;
        }

        const int atlasMargin = 12;
        const int cellGap = 12;
        const int maximumColumnHeight = 1400;
        const double scale = 2.5;
        var mappedCells = new List<AtlasCell>(sourceCells.Count);
        var left = atlasMargin;
        var top = atlasMargin;
        var columnWidth = 0;
        var atlasWidth = atlasMargin * 2;
        var atlasHeight = atlasMargin * 2;
        foreach (var sourceCell in sourceCells)
        {
            var destinationWidth = Math.Max(
                1,
                (int)Math.Ceiling(sourceCell.Content.Width * scale));
            var destinationHeight = Math.Max(
                1,
                (int)Math.Ceiling(sourceCell.Content.Height * scale));
            if (top > atlasMargin &&
                top + destinationHeight + atlasMargin > maximumColumnHeight)
            {
                left += columnWidth + cellGap;
                top = atlasMargin;
                columnWidth = 0;
            }

            var destination = new Rectangle(
                left,
                top,
                destinationWidth,
                destinationHeight);
            mappedCells.Add(new AtlasCell(
                sourceCell.Source,
                destination,
                sourceCell.DirectText));
            top = destination.Bottom + cellGap;
            columnWidth = Math.Max(columnWidth, destination.Width);
            atlasWidth = Math.Max(atlasWidth, destination.Right + atlasMargin);
            atlasHeight = Math.Max(atlasHeight, destination.Bottom + atlasMargin);
        }

        var atlas = new Bitmap(
            Math.Max(32, atlasWidth),
            Math.Max(32, atlasHeight),
            PixelFormat.Format32bppPArgb);
        using (var graphics = Graphics.FromImage(atlas))
        {
            graphics.Clear(Color.White);
            graphics.CompositingMode = CompositingMode.SourceCopy;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            for (var index = 0; index < mappedCells.Count; index++)
            {
                graphics.DrawImage(
                    sourceCells[index].Content,
                    mappedCells[index].Destination);
            }
        }

        foreach (var sourceCell in sourceCells)
        {
            sourceCell.Content.Dispose();
        }

        atlasCells = mappedCells;
        return new CapturedImage(atlas);
    }

    private static Bitmap? BuildHorizontalCell(
        Bitmap source,
        Rectangle sourceRectangle,
        out IReadOnlyList<Rectangle> glyphRectangles)
    {
        using var crop = source.Clone(
            sourceRectangle,
            PixelFormat.Format32bppPArgb);
        var activeRows = new bool[crop.Height];
        for (var y = 1; y < crop.Height - 1; y++)
        {
            var darkPixels = 0;
            for (var x = 1; x < crop.Width - 1; x++)
            {
                var color = crop.GetPixel(x, y);
                var luminance = (color.R * 299 +
                                 color.G * 587 +
                                 color.B * 114) / 1000;
                if (luminance < 175)
                {
                    darkPixels++;
                }
            }

            activeRows[y] = darkPixels >= 2;
        }

        var bands = new List<(int Top, int Bottom)>();
        var bandTop = -1;
        var lastActive = -1;
        for (var y = 1; y < crop.Height - 1; y++)
        {
            if (activeRows[y])
            {
                if (bandTop < 0)
                {
                    bandTop = y;
                }
                lastActive = y;
                continue;
            }

            if (bandTop >= 0 && y - lastActive > 3)
            {
                AddBand(bandTop, lastActive);
                bandTop = -1;
                lastActive = -1;
            }
        }
        if (bandTop >= 0)
        {
            AddBand(bandTop, lastActive);
        }

        if (bands.Count == 0)
        {
            glyphRectangles = [];
            return null;
        }

        var glyphs = new List<Rectangle>(bands.Count);
        foreach (var band in bands)
        {
            var left = crop.Width;
            var right = -1;
            for (var y = band.Top; y <= band.Bottom; y++)
            {
                for (var x = 1; x < crop.Width - 1; x++)
                {
                    var color = crop.GetPixel(x, y);
                    var luminance = (color.R * 299 +
                                     color.G * 587 +
                                     color.B * 114) / 1000;
                    if (luminance >= 190)
                    {
                        continue;
                    }

                    left = Math.Min(left, x);
                    right = Math.Max(right, x);
                }
            }

            if (right >= left)
            {
                glyphs.Add(Rectangle.FromLTRB(
                    Math.Max(0, left - 2),
                    Math.Max(0, band.Top - 2),
                    Math.Min(crop.Width, right + 3),
                    Math.Min(crop.Height, band.Bottom + 3)));
            }
        }

        if (glyphs.Count == 0)
        {
            glyphRectangles = [];
            return null;
        }

        const int padding = 8;
        const int gap = 3;
        var contentWidth = glyphs.Sum(glyph => glyph.Width) +
                           ((glyphs.Count - 1) * gap);
        var contentHeight = glyphs.Max(glyph => glyph.Height);
        var result = new Bitmap(
            contentWidth + (padding * 2),
            contentHeight + (padding * 2),
            PixelFormat.Format32bppPArgb);
        using (var graphics = Graphics.FromImage(result))
        {
            graphics.Clear(Color.White);
            graphics.CompositingMode = CompositingMode.SourceOver;
            var x = padding;
            foreach (var glyph in glyphs)
            {
                var y = padding + ((contentHeight - glyph.Height) / 2);
                graphics.DrawImage(
                    crop,
                    new Rectangle(x, y, glyph.Width, glyph.Height),
                    glyph,
                    GraphicsUnit.Pixel);
                x += glyph.Width + gap;
            }
        }

        glyphRectangles = glyphs;
        return result;

        void AddBand(int top, int bottom)
        {
            if (bottom - top + 1 >= 4)
            {
                bands.Add((top, bottom));
            }
        }
    }

    private static string? RecognizeStackedDigits(
        Bitmap source,
        Rectangle sourceRectangle,
        IReadOnlyList<Rectangle> glyphs,
        IReadOnlyList<OcrWordRegion> primaryWords)
    {
        if (glyphs.Count is < 1 or > 8)
        {
            return null;
        }

        var templates = primaryWords
            .Select(word => (
                Text: NormalizeDigits(string.Concat(word.Text.Where(
                    character => !char.IsWhiteSpace(character)))),
                Rectangle: Rectangle.FromLTRB(
                    Math.Max(0, (int)Math.Floor(word.X) - 1),
                    Math.Max(0, (int)Math.Floor(word.Y) - 1),
                    Math.Min(source.Width, (int)Math.Ceiling(word.X + word.Width) + 1),
                    Math.Min(source.Height, (int)Math.Ceiling(word.Y + word.Height) + 1))))
            .Where(template =>
                template.Text.Length == 1 &&
                char.IsDigit(template.Text[0]) &&
                template.Rectangle.Width > 2 &&
                template.Rectangle.Height > 4)
            .ToArray();
        if (templates.Length < 3)
        {
            return null;
        }

        var result = new char[glyphs.Count];
        for (var index = 0; index < glyphs.Count; index++)
        {
            var glyph = glyphs[index];
            if (glyph.Width > glyph.Height * 1.05)
            {
                return null;
            }

            var absoluteGlyph = new Rectangle(
                sourceRectangle.X + glyph.X,
                sourceRectangle.Y + glyph.Y,
                glyph.Width,
                glyph.Height);
            var scores = templates
                .Select(template => (
                    Digit: template.Text[0],
                    Score: CompareGlyphs(
                        source,
                        absoluteGlyph,
                        template.Rectangle)))
                .GroupBy(item => item.Digit)
                .Select(group => (
                    Digit: group.Key,
                    Score: group.Max(item => item.Score)))
                .OrderByDescending(item => item.Score)
                .ToArray();
            if (scores.Length == 0 || scores[0].Score < 0.58 ||
                scores.Length > 1 && scores[0].Score - scores[1].Score < 0.025)
            {
                return null;
            }

            result[index] = scores[0].Digit;
        }

        return new string(result);
    }

    private static double CompareGlyphs(
        Bitmap source,
        Rectangle first,
        Rectangle second)
    {
        const int maskSize = 24;
        var firstMask = BuildGlyphMask(source, first, maskSize);
        var secondMask = BuildGlyphMask(source, second, maskSize);
        var intersection = 0;
        var union = 0;
        for (var index = 0; index < firstMask.Length; index++)
        {
            if (firstMask[index] || secondMask[index])
            {
                union++;
            }
            if (firstMask[index] && secondMask[index])
            {
                intersection++;
            }
        }

        return union == 0 ? 0 : intersection / (double)union;
    }

    private static bool[] BuildGlyphMask(
        Bitmap source,
        Rectangle rectangle,
        int size)
    {
        rectangle.Intersect(new Rectangle(0, 0, source.Width, source.Height));
        var left = rectangle.Right;
        var top = rectangle.Bottom;
        var right = rectangle.Left - 1;
        var bottom = rectangle.Top - 1;
        for (var y = rectangle.Top; y < rectangle.Bottom; y++)
        {
            for (var x = rectangle.Left; x < rectangle.Right; x++)
            {
                var color = source.GetPixel(x, y);
                var luminance = (color.R * 299 +
                                 color.G * 587 +
                                 color.B * 114) / 1000;
                if (luminance >= 185)
                {
                    continue;
                }

                left = Math.Min(left, x);
                top = Math.Min(top, y);
                right = Math.Max(right, x);
                bottom = Math.Max(bottom, y);
            }
        }

        var mask = new bool[size * size];
        if (right < left || bottom < top)
        {
            return mask;
        }

        var width = right - left + 1;
        var height = bottom - top + 1;
        for (var outputY = 0; outputY < size; outputY++)
        {
            var sourceY = top + Math.Min(
                height - 1,
                (outputY * height) / size);
            for (var outputX = 0; outputX < size; outputX++)
            {
                var sourceX = left + Math.Min(
                    width - 1,
                    (outputX * width) / size);
                var color = source.GetPixel(sourceX, sourceY);
                var luminance = (color.R * 299 +
                                 color.G * 587 +
                                 color.B * 114) / 1000;
                mask[(outputY * size) + outputX] = luminance < 185;
            }
        }

        return mask;
    }

    private static string? NormalizeAtlasText(string value)
    {
        var compact = string.Concat(value.Where(character =>
            !char.IsWhiteSpace(character)));
        if (compact.Length == 0)
        {
            return null;
        }

        compact = compact
            .Replace("、", string.Empty, StringComparison.Ordinal)
            .Replace("，", string.Empty, StringComparison.Ordinal)
            .Replace(",", string.Empty, StringComparison.Ordinal)
            .Replace("．", string.Empty, StringComparison.Ordinal)
            .Replace(".", string.Empty, StringComparison.Ordinal);

        var monthIndex = compact.IndexOf('月');
        var dayIndex = compact.IndexOf('日', monthIndex + 1);
        if (monthIndex is >= 1 and <= 2 && dayIndex > monthIndex + 1)
        {
            var monthText = NormalizeDigits(compact[..monthIndex]);
            var dayText = NormalizeDigits(compact[(monthIndex + 1)..dayIndex]);
            if (int.TryParse(monthText, NumberStyles.None, CultureInfo.InvariantCulture, out var month) &&
                int.TryParse(dayText, NumberStyles.None, CultureInfo.InvariantCulture, out var day) &&
                month is >= 1 and <= 12 && day is >= 1 and <= 31)
            {
                return $"{month}月{day}日";
            }
        }

        var normalizedDigits = NormalizeDigits(compact);
        if (normalizedDigits.Length is >= 1 and <= 8 &&
            normalizedDigits.All(char.IsDigit))
        {
            return normalizedDigits;
        }

        if (compact.EndsWith('%') &&
            normalizedDigits[..^1].Length is >= 1 and <= 3 &&
            normalizedDigits[..^1].All(char.IsDigit))
        {
            return normalizedDigits;
        }

        var status = compact.Replace('0', 'O');
        if (status.StartsWith('O') &&
            status.All(character =>
                char.IsLetter(character) || character == '-') &&
            (status.Contains("go", StringComparison.OrdinalIgnoreCase) ||
             status.Contains("in", StringComparison.OrdinalIgnoreCase)))
        {
            return "On-going";
        }

        return compact is "项目" or "开始时间" or "结束时间" or
            "状态" or "完成比例"
            ? compact
            : null;
    }

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

    private static bool ContainsCenter(
        Rectangle rectangle,
        OcrWordRegion word)
    {
        var centerX = word.X + (word.Width / 2);
        var centerY = word.Y + (word.Height / 2);
        return centerX >= rectangle.Left && centerX <= rectangle.Right &&
               centerY >= rectangle.Top && centerY <= rectangle.Bottom;
    }

    private sealed record AtlasCell(
        Rectangle Source,
        Rectangle Destination,
        string? DirectText);
}
