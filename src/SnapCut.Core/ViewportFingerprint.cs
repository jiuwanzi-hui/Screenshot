namespace SnapCut.Core;

/// <summary>
/// A compact, texture-weighted description of a captured viewport. It is used
/// only to recognize a viewport that has already been seen, so a return scroll
/// can move the active anchor without appending the same content again.
/// </summary>
internal sealed class ViewportFingerprint
{
    private const int SampleColumns = 64;
    private const int SampleRows = 96;
    private const double MaximumAverageDifference = 0.004;
    private const double MaximumChangedWeightRatio = 0.004;
    private const double MaximumStationaryBodyAverageDifference = 0.045;
    private const double MaximumStationaryBodyChangedWeightRatio = 0.30;
    private const double MaximumStationaryAnchorAverageDifference = 0.012;
    private const double MaximumStationaryAnchorChangedWeightRatio = 0.06;
    private readonly uint[] _samples;

    private ViewportFingerprint(uint[] samples)
    {
        _samples = samples;
    }

    public static ViewportFingerprint Create(PixelImage image)
    {
        ArgumentNullException.ThrowIfNull(image);

        var pixels = image.Pixels;
        var stride = image.Stride;
        var samples = new uint[SampleColumns * SampleRows];
        var left = 0;
        var right = image.Width >= 80
            ? image.Width - Math.Clamp(image.Width / 80, 10, 24)
            : image.Width;
        var top = image.Height >= 120 ? image.Height / 8 : 0;
        var bottom = image.Height >= 120
            ? image.Height - Math.Min(24, image.Height / 30)
            : image.Height;

        if (right - left < 3 || bottom - top < 3)
        {
            left = 0;
            right = image.Width;
            top = 0;
            bottom = image.Height;
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
                    image.Width,
                    image.Height,
                    x,
                    y);
                var horizontalNeighbor = GetAverageColor(
                    pixels,
                    stride,
                    image.Width,
                    image.Height,
                    x + 3,
                    y);
                var verticalNeighbor = GetAverageColor(
                    pixels,
                    stride,
                    image.Width,
                    image.Height,
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

        return new ViewportFingerprint(samples);
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

        // The compositor and fractional scrolling can move an otherwise
        // unchanged viewport by a few physical pixels. At this sampling density
        // that is at most one sample row. Requiring the body and the left-side
        // structural anchor to agree prevents repeated code blocks elsewhere in
        // the document from being mistaken for tiny motion.
        for (var rowOffset = -3; rowOffset <= 3; rowOffset++)
        {
            var body = MeasureBandDifference(
                other,
                rowOffset,
                0,
                SampleColumns);
            var anchor = MeasureBandDifference(
                other,
                rowOffset,
                0,
                SampleColumns / 6);

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

    private DifferenceMetrics? MeasureBandDifference(
        ViewportFingerprint other,
        int rowOffset,
        int firstColumn,
        int lastColumn)
    {
        if (_samples.Length != other._samples.Length ||
            firstColumn < 0 ||
            lastColumn > SampleColumns ||
            firstColumn >= lastColumn ||
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
            var leftRow = (leftStartRow + row) * SampleColumns;
            var rightRow = (rightStartRow + row) * SampleColumns;

            for (var column = firstColumn; column < lastColumn; column++)
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
