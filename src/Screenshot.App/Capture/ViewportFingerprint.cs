using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Screenshot.App.Capture;

/// <summary>
/// A compact, texture-weighted description of a captured viewport. It is used
/// only to recognize a viewport that has already been seen, so a return scroll
/// can move the active anchor without appending the same content again.
/// </summary>
internal sealed class ViewportFingerprint
{
    private const int SampleColumns = 64;
    // Keep vertical sampling close to pixel resolution. Return samples rarely
    // land on exactly the same scroll row; a coarse 96-row grid quantized a
    // 30-40px return shift by several pixels and thin chat/table separators no
    // longer aligned, so the historical locator selected a periodic row instead.
    private const int SampleRows = 256;
    private const double MaximumAverageDifference = 0.004;
    private const double MaximumChangedWeightRatio = 0.004;
    private const double MaximumStationaryBodyAverageDifference = 0.045;
    private const double MaximumStationaryBodyChangedWeightRatio = 0.30;
    private const double MaximumStationaryAnchorAverageDifference = 0.012;
    private const double MaximumStationaryAnchorChangedWeightRatio = 0.06;
    private const double MaximumReturnBodyAverageDifference = 0.035;
    private const double MaximumReturnBodyChangedWeightRatio = 0.22;
    private const double MaximumReturnAnchorAverageDifference = 0.011;
    private const double MaximumReturnAnchorChangedWeightRatio = 0.055;
    private const double MaximumShiftedReturnScore = 0.20;
    private readonly uint[] _samples;
    private readonly int _sampledPixelSpan;

    public int SampledPixelSpan => _sampledPixelSpan;

    public int SourceHeight { get; }

    private ViewportFingerprint(
        uint[] samples,
        int sampledPixelSpan,
        int sourceHeight)
    {
        _samples = samples;
        _sampledPixelSpan = sampledPixelSpan;
        SourceHeight = sourceHeight;
    }

    public static ViewportFingerprint Create(Bitmap bitmap)
    {
        ArgumentNullException.ThrowIfNull(bitmap);

        Bitmap? convertedBitmap = null;
        var source = bitmap;

        if (Image.GetPixelFormatSize(bitmap.PixelFormat) != 32)
        {
            convertedBitmap = new Bitmap(
                bitmap.Width,
                bitmap.Height,
                PixelFormat.Format32bppPArgb);
            using var graphics = Graphics.FromImage(convertedBitmap);
            graphics.DrawImageUnscaled(bitmap, 0, 0);
            source = convertedBitmap;
        }

        try
        {
            var rectangle = new Rectangle(0, 0, source.Width, source.Height);
            var bitmapData = source.LockBits(
                rectangle,
                ImageLockMode.ReadOnly,
                source.PixelFormat);

            try
            {
                var stride = Math.Abs(bitmapData.Stride);
                var pixels = new byte[stride * source.Height];
                Marshal.Copy(bitmapData.Scan0, pixels, 0, pixels.Length);

                var samples = new uint[SampleColumns * SampleRows];
                var left = 0;
                var right = source.Width >= 80
                    ? source.Width - Math.Clamp(source.Width / 80, 10, 24)
                    : source.Width;
                var top = source.Height >= 120 ? source.Height / 8 : 0;
                var bottom = source.Height >= 120
                    ? source.Height - Math.Min(24, source.Height / 30)
                    : source.Height;

                if (right - left < 3 || bottom - top < 3)
                {
                    left = 0;
                    right = source.Width;
                    top = 0;
                    bottom = source.Height;
                }

                for (var sampleY = 0; sampleY < SampleRows; sampleY++)
                {
                    var y = GetSampleCoordinate(
                        top,
                        bottom - 2,
                        sampleY,
                        SampleRows);

                    for (var sampleX = 0; sampleX < SampleColumns; sampleX++)
                    {
                        var x = GetSampleCoordinate(
                            left,
                            right - 2,
                            sampleX,
                            SampleColumns);
                        var color = GetAverageColor(
                            pixels,
                            stride,
                            source.Width,
                            source.Height,
                            x,
                            y);
                        var horizontalNeighbor = GetAverageColor(
                            pixels,
                            stride,
                            source.Width,
                            source.Height,
                            x + 3,
                            y);
                        var verticalNeighbor = GetAverageColor(
                            pixels,
                            stride,
                            source.Width,
                            source.Height,
                            x,
                            y + 3);
                        var texture = Math.Min(
                            byte.MaxValue,
                            16 + GetColorDifference(color, horizontalNeighbor) +
                            GetColorDifference(color, verticalNeighbor));
                        samples[(sampleY * SampleColumns) + sampleX] =
                            ((uint)texture << 24) |
                            ((uint)color.R << 16) |
                            ((uint)color.G << 8) |
                            color.B;
                    }
                }

                return new ViewportFingerprint(
                    samples,
                    Math.Max(1, bottom - 2 - top),
                    source.Height);
            }
            finally
            {
                source.UnlockBits(bitmapData);
            }
        }
        finally
        {
            convertedBitmap?.Dispose();
        }
    }

    public bool IsSimilarTo(ViewportFingerprint other)
    {
        var difference = MeasureDifference(other);
        return difference is { } metrics &&
               metrics.Average <= MaximumAverageDifference &&
               metrics.ChangedWeightRatio <= MaximumChangedWeightRatio;
    }

    public bool IsStationaryComparedTo(ViewportFingerprint other)
    {
        ArgumentNullException.ThrowIfNull(other);

        // DWM and fractional scrolling can move an otherwise unchanged viewport
        // by a few physical pixels. At this sampling density that is at most one
        // sample row. Requiring the body and the left-side structural anchor to
        // agree prevents repeated code blocks elsewhere in the document from
        // being mistaken for tiny motion.
        for (var rowOffset = -3; rowOffset <= 3; rowOffset++)
        {
            var body = MeasureBandDifference(
                other,
                rowOffset,
                0,
                SampleColumns,
                SampleRows / 5,
                SampleRows);
            var anchor = MeasureBandDifference(
                other,
                rowOffset,
                0,
                SampleColumns / 6,
                SampleRows / 5,
                SampleRows);

            if (body is { } bodyMetrics &&
                anchor is { } anchorMetrics &&
                bodyMetrics.Average <= MaximumStationaryBodyAverageDifference &&
                bodyMetrics.ChangedWeightRatio <= MaximumStationaryBodyChangedWeightRatio &&
                anchorMetrics.Average <= MaximumStationaryAnchorAverageDifference &&
                anchorMetrics.ChangedWeightRatio <= MaximumStationaryAnchorChangedWeightRatio)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Recognizes a previously captured viewport when live cells, hover states,
    /// or a scrollbar have changed since it was first seen.
    /// </summary>
    public bool IsPreviouslySeenComparedTo(ViewportFingerprint other)
    {
        ArgumentNullException.ThrowIfNull(other);

        // This deliberately sits between the pixel-strict historical match and
        // the looser stationary-boundary check. The narrow left-side band acts
        // as a structural page anchor; dynamic dashboard values normally live
        // outside it, while repeated table rows alone cannot satisfy it.
        // Historical anchors must not absorb a genuine one- or two-pixel
        // document movement. The current-anchor stationary check already owns
        // DWM/fractional-scroll jitter; return matching compares the same sample
        // coordinates so it cannot introduce cumulative vertical drift.
        var body = MeasureBandDifference(
            other,
            rowOffset: 0,
            0,
            SampleColumns);
        var anchor = MeasureBandDifference(
            other,
            rowOffset: 0,
            0,
            SampleColumns / 6);

        return body is { } bodyMetrics &&
               anchor is { } anchorMetrics &&
               bodyMetrics.Average <= MaximumReturnBodyAverageDifference &&
               bodyMetrics.ChangedWeightRatio <= MaximumReturnBodyChangedWeightRatio &&
               anchorMetrics.Average <= MaximumReturnAnchorAverageDifference &&
               anchorMetrics.ChangedWeightRatio <= MaximumReturnAnchorChangedWeightRatio;
    }

    /// <summary>
    /// Locates the same viewport content when the return sample was taken a
    /// little before or after its historical sample. The returned pixel shift
    /// is absolute relative to this fingerprint: positive means the new
    /// viewport is further down the document.
    /// </summary>
    public bool TryLocatePreviouslySeenComparedTo(
        ViewportFingerprint other,
        int maximumPixelShift,
        out int pixelShift,
        out double score)
    {
        ArgumentNullException.ThrowIfNull(other);

        pixelShift = 0;
        score = double.MaxValue;
        if (_samples.Length != other._samples.Length ||
            maximumPixelShift <= 0)
        {
            return false;
        }

        var pixelsPerSampleRow = _sampledPixelSpan /
            (double)(SampleRows - 1);
        var maximumRowOffset = Math.Clamp(
            (int)Math.Ceiling(maximumPixelShift /
                Math.Max(1d, pixelsPerSampleRow)),
            1,
            SampleRows / 3);
        var found = false;
        var bestAcceptedScore = double.MaxValue;
        var bestObservedScore = double.MaxValue;
        var bestObservedShift = 0;
        var coarseCandidates = new List<(int RowOffset, double Score)>();

        for (var rowOffset = -maximumRowOffset;
             rowOffset <= maximumRowOffset;
             rowOffset++)
        {
            if (rowOffset == 0)
            {
                continue;
            }

            var body = MeasureBandDifference(
                other,
                rowOffset,
                0,
                SampleColumns,
                columnStride: 4);
            var anchor = MeasureBandDifference(
                other,
                rowOffset,
                0,
                SampleColumns / 6,
                columnStride: 2);
            if (body is not { } bodyMetrics ||
                anchor is not { } anchorMetrics)
            {
                continue;
            }

            var coarseScore =
                bodyMetrics.Average +
                (bodyMetrics.ChangedWeightRatio * 0.08) +
                (anchorMetrics.Average * 2d) +
                (anchorMetrics.ChangedWeightRatio * 0.16);
            coarseCandidates.Add((rowOffset, coarseScore));
        }

        // Evaluating every color sample at every possible shift for every
        // historical viewport made one reverse frame take hundreds of
        // milliseconds. The sparse pass above keeps the structurally best
        // shifts; only those finalists pay for the full comparison.
        foreach (var candidate in coarseCandidates
                     .OrderBy(candidate => candidate.Score)
                     .Take(8))
        {
            var body = MeasureBandDifference(
                other,
                candidate.RowOffset,
                0,
                SampleColumns);
            var anchor = MeasureBandDifference(
                other,
                candidate.RowOffset,
                0,
                SampleColumns / 6);
            if (body is not { } bodyMetrics ||
                anchor is not { } anchorMetrics)
            {
                continue;
            }

            var candidateScore =
                bodyMetrics.Average +
                (bodyMetrics.ChangedWeightRatio * 0.08) +
                (anchorMetrics.Average * 2d) +
                (anchorMetrics.ChangedWeightRatio * 0.16);
            if (candidateScore < bestObservedScore)
            {
                bestObservedScore = candidateScore;
                bestObservedShift = (int)Math.Round(
                    -candidate.RowOffset * pixelsPerSampleRow);
            }

            if (candidateScore > MaximumShiftedReturnScore)
            {
                continue;
            }

            if (candidateScore >= bestAcceptedScore)
            {
                continue;
            }

            found = true;
            bestAcceptedScore = candidateScore;
            score = candidateScore;
            pixelShift = (int)Math.Round(
                -candidate.RowOffset * pixelsPerSampleRow);
        }

        if (!found)
        {
            score = bestObservedScore;
            pixelShift = bestObservedShift;
        }

        return found;
    }

    private DifferenceMetrics? MeasureBandDifference(
        ViewportFingerprint other,
        int rowOffset,
        int firstColumn,
        int lastColumn,
        int firstSampleRow = 0,
        int lastSampleRow = SampleRows,
        int columnStride = 1)
    {
        if (_samples.Length != other._samples.Length ||
            firstColumn < 0 ||
            lastColumn > SampleColumns ||
            firstColumn >= lastColumn ||
            firstSampleRow < 0 ||
            lastSampleRow > SampleRows ||
            firstSampleRow >= lastSampleRow ||
            columnStride <= 0 ||
            Math.Abs(rowOffset) >= SampleRows)
        {
            return null;
        }

        var leftStartRow = Math.Max(0, -rowOffset);
        var rightStartRow = Math.Max(0, rowOffset);
        var comparedRows = SampleRows - Math.Abs(rowOffset);
        long totalDifference = 0;
        long totalWeight = 0;
        long changedWeight = 0;

        for (var row = 0; row < comparedRows; row++)
        {
            var leftSampleRow = leftStartRow + row;
            var rightSampleRow = rightStartRow + row;
            if (leftSampleRow < firstSampleRow ||
                leftSampleRow >= lastSampleRow ||
                rightSampleRow < firstSampleRow ||
                rightSampleRow >= lastSampleRow)
            {
                continue;
            }

            var leftRow = leftSampleRow * SampleColumns;
            var rightRow = rightSampleRow * SampleColumns;

            for (var column = firstColumn;
                 column < lastColumn;
                 column += columnStride)
            {
                var left = _samples[leftRow + column];
                var right = other._samples[rightRow + column];
                var weight = Math.Max(left >> 24, right >> 24);
                var redDifference = Math.Abs(
                    (int)((left >> 16) & byte.MaxValue) -
                    (int)((right >> 16) & byte.MaxValue));
                var greenDifference = Math.Abs(
                    (int)((left >> 8) & byte.MaxValue) -
                    (int)((right >> 8) & byte.MaxValue));
                var blueDifference = Math.Abs(
                    (int)(left & byte.MaxValue) -
                    (int)(right & byte.MaxValue));
                var difference = redDifference + greenDifference + blueDifference;

                totalDifference += difference * weight;
                totalWeight += weight;
                if (difference > 36)
                {
                    changedWeight += weight;
                }
            }
        }

        if (totalWeight == 0)
        {
            return null;
        }

        return new DifferenceMetrics(
            totalDifference / (totalWeight * 255d * 3d),
            changedWeight / (double)totalWeight);
    }

    private DifferenceMetrics? MeasureDifference(ViewportFingerprint other)
    {
        ArgumentNullException.ThrowIfNull(other);

        if (_samples.Length != other._samples.Length)
        {
            return null;
        }

        long totalDifference = 0;
        long totalWeight = 0;
        long changedWeight = 0;

        for (var index = 0; index < _samples.Length; index++)
        {
            var left = _samples[index];
            var right = other._samples[index];
            var weight = Math.Max(left >> 24, right >> 24);
            if (index % SampleColumns < SampleColumns / 8)
            {
                weight *= 4;
            }
            var redDifference = Math.Abs(
                (int)((left >> 16) & byte.MaxValue) -
                (int)((right >> 16) & byte.MaxValue));
            var greenDifference = Math.Abs(
                (int)((left >> 8) & byte.MaxValue) -
                (int)((right >> 8) & byte.MaxValue));
            var blueDifference = Math.Abs(
                (int)(left & byte.MaxValue) -
                (int)(right & byte.MaxValue));
            var difference = redDifference + greenDifference + blueDifference;

            totalDifference += difference * weight;
            totalWeight += weight;

            if (difference > 36)
            {
                changedWeight += weight;
            }
        }

        if (totalWeight == 0)
        {
            return null;
        }

        var averageDifference = totalDifference / (totalWeight * 255d * 3d);
        var changedWeightRatio = changedWeight / (double)totalWeight;
        return new DifferenceMetrics(averageDifference, changedWeightRatio);
    }

    private static int GetSampleCoordinate(
        int minimum,
        int maximum,
        int index,
        int count)
    {
        if (count <= 1 || maximum <= minimum)
        {
            return minimum;
        }

        return minimum + ((maximum - minimum) * index / (count - 1));
    }

    private static RgbColor GetColor(byte[] pixels, int stride, int x, int y)
    {
        var offset = (y * stride) + (x * 4);
        return new RgbColor(
            pixels[offset + 2],
            pixels[offset + 1],
            pixels[offset]);
    }

    private static RgbColor GetAverageColor(
        byte[] pixels,
        int stride,
        int width,
        int height,
        int centerX,
        int centerY)
    {
        const int radius = 2;
        var left = Math.Max(0, centerX - radius);
        var right = Math.Min(width - 1, centerX + radius);
        var top = Math.Max(0, centerY - radius);
        var bottom = Math.Min(height - 1, centerY + radius);
        var red = 0;
        var green = 0;
        var blue = 0;
        var count = 0;

        for (var y = top; y <= bottom; y++)
        {
            for (var x = left; x <= right; x++)
            {
                var color = GetColor(pixels, stride, x, y);
                red += color.R;
                green += color.G;
                blue += color.B;
                count++;
            }
        }

        return new RgbColor(
            (byte)(red / count),
            (byte)(green / count),
            (byte)(blue / count));
    }

    private static int GetColorDifference(RgbColor left, RgbColor right)
    {
        return Math.Abs(left.R - right.R) +
               Math.Abs(left.G - right.G) +
               Math.Abs(left.B - right.B);
    }

    private readonly record struct RgbColor(byte R, byte G, byte B);

    private readonly record struct DifferenceMetrics(
        double Average,
        double ChangedWeightRatio);
}
