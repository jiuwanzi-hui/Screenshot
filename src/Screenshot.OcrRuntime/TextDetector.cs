// Apache-2.0 license
// Adapted from RapidAI / RapidOCR
// https://github.com/RapidAI/RapidOCR/blob/92aec2c1234597fa9c3c270efd2600c83feecd8d/dotnet/RapidOcrOnnxCs/OcrLib/DbNet.cs

using System.Buffers;
using Clipper2Lib;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SkiaSharp;

namespace RapidOcrNet;

public sealed class TextDetector : IDisposable
{
    private const float DilateRadius = 1f;
    private readonly SKPaint _dilatePaint;
    private readonly SKImageFilter _dilateFilter;

    // NOTE: the PP-OCRv5 detector ONNX bundled with this repo was empirically trained with
    // ImageNet normalization, NOT the (0.5,0.5,0.5)/(0.5,0.5,0.5) constants the Python
    // rapidocr config ships (its config.yaml targets PP-OCRv4). Reverting to PP-OCR's
    // canonical 0.5/0.5 here causes the detector to miss most boxes. This stays the default
    // so callers using the parameterless-norm InitModel overload keep v5 behavior. PP-OCRv6
    // models use 0.5/0.5 instead, passed in explicitly via the mean/std InitModel overload.
    private static readonly float[] DefaultMeanValues = [0.485F * 255F, 0.456F * 255F, 0.406F * 255F];
    private static readonly float[] DefaultNormValues = [1.0F / 0.229F / 255.0F, 1.0F / 0.224F / 255.0F, 1.0F / 0.225F / 255.0F];

    // Per-instance normalization, set at InitModel time. Defaults to ImageNet (v5).
    private float[] _meanValues = DefaultMeanValues;
    private float[] _normValues = DefaultNormValues;

    private InferenceSession _dbNet;
    private string _inputName;

    public TextDetector()
    {
        _dilateFilter = SKImageFilter.CreateDilate(DilateRadius, DilateRadius);
        _dilatePaint = new SKPaint
        {
            ImageFilter = _dilateFilter
        };
    }

    public void InitModel(string path, SessionOptions op)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Detector model file does not exist: '{path}'.");
        }

        _dbNet = new InferenceSession(path, op);
        _inputName = _dbNet.InputMetadata.Keys.First();
    }

    /// <summary>
    /// Initialize the detector with explicit pixel-space normalization. <paramref name="mean"/>
    /// and <paramref name="std"/> are in pixel space (e.g. 0.5 maps to 127.5). The std is
    /// inverted internally so the existing <c>(pixel - mean) * (1/std)</c> normalization math
    /// is preserved. Use this for PP-OCRv6 detectors, which expect mean/std (127.5, 127.5).
    /// </summary>
    public void InitModel(string path, float[] mean, float[] std, SessionOptions op)
    {
        ArgumentNullException.ThrowIfNull(mean);
        ArgumentNullException.ThrowIfNull(std);
        if (mean.Length != 3 || std.Length != 3)
        {
            throw new ArgumentException("Detector mean and std must each have exactly 3 channel values.");
        }

        InitModel(path, op);

        _meanValues = mean;
        var norm = new float[3];
        for (int c = 0; c < 3; c++)
        {
            if (std[c] == 0F)
            {
                throw new ArgumentException("Detector std values must be non-zero.", nameof(std));
            }

            norm[c] = 1.0F / std[c];
        }

        _normValues = norm;
    }

    public void InitModel(string path, int numThread)
    {
        using var sessionOptions = RapidOcr.GetDefaultSessionOptions(numThread);
        InitModel(path, sessionOptions);
    }

    public IReadOnlyList<TextBox>? GetTextBoxes(SKBitmap src, ScaleParam scale, float boxScoreThresh, float boxThresh,
        float unClipRatio)
    {
        Tensor<float> inputTensors;
        using (var srcResize = src.Resize(new SKSizeI(scale.DstWidth, scale.DstHeight), OcrUtils.NetworkSampling))
        {
            /*
#if DEBUG
            using (var fs = new FileStream($"Detector_{Guid.NewGuid()}.png", FileMode.Create))
            {
                srcResize.Encode(fs, SKEncodedImageFormat.Png, 100);
            }
#endif
            */

            inputTensors = OcrUtils.SubtractMeanNormalize(srcResize, _meanValues, _normValues);
        }

        IReadOnlyCollection<NamedOnnxValue> inputs = new NamedOnnxValue[]
        {
                NamedOnnxValue.CreateFromTensor(_inputName, inputTensors)
        };

        try
        {
            using (IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results = _dbNet.Run(inputs))
            {
                return GetTextBoxes(results[0], scale.DstHeight, scale.DstWidth, scale, boxScoreThresh,
                    boxThresh, unClipRatio);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(ex.Message + ex.StackTrace);
        }

        return null;
    }

    private static SKPoint[][] FindContours(ReadOnlySpan<byte> array, int rows, int cols)
    {
        int[]? vPool = null;
        try
        {
            Span<int> v = array.Length <= 256 ? stackalloc int[array.Length] : vPool = ArrayPool<int>.Shared.Rent(array.Length);

            for (int i = 0; i < array.Length; i++)
            {
                v[i] = array[i];
            }

            var contours = PContour.FindContours(v, cols, rows);

            var result = new List<SKPoint[]>(contours.Count);
            foreach (var c in contours)
            {
                if (!c.isHole)
                {
                    result.Add(PContour.ApproxPolyDP(c.GetSpan(), 1).ToArray());
                }
            }
            return result.ToArray();
        }
        finally
        {
            if (vPool is not null)
            {
                ArrayPool<int>.Shared.Return(vPool);
            }
        }
    }


    private IReadOnlyList<TextBox> GetTextBoxes(DisposableNamedOnnxValue outputTensor, int rows, int cols,
        ScaleParam s, float boxScoreThresh, float boxThresh, float unClipRatio)
    {
        const float maxSideThresh = 3.0f; // Long Edge Threshold
        var rsBoxes = new List<TextBox>();

        // Data preparation
        ReadOnlySpan<float> predData;
        if (outputTensor.AsTensor<float>() is DenseTensor<float> dt)
        {
            predData = dt.Buffer.Span;
        }
        else
        {
            predData = outputTensor.AsEnumerable<float>().ToArray();
        }

        var gray8 = new SKImageInfo()
        {
            Height = rows,
            Width = cols,
            AlphaType = SKAlphaType.Opaque,
            ColorType = SKColorType.Gray8
        };

        using var predImage = new SKBitmap(gray8);
        using var thresholdMatBitmap = new SKBitmap(gray8);

        SKPoint[][] contours;

        Span<byte> cbufMat = predImage.GetPixelSpan();
        Span<byte> thresholdMat = thresholdMatBitmap.GetPixelSpan();

        for (int i = 0; i < predData.Length; i++)
        {
            float f = predData[i];
            cbufMat[i] = (byte)MathF.Round(f * 255f);
            thresholdMat[i] = f > boxThresh ? (byte)1 : (byte)0; // Thresholding
        }

        using (var canvas = new SKCanvas(thresholdMatBitmap))
        {
            canvas.DrawBitmap(thresholdMatBitmap, 0, 0, _dilatePaint);
            // TODO - Check dilate by rendering thresholdMatBitmap to file

            contours = FindContours(thresholdMat, rows, cols);
        }

        for (int i = 0; i < contours.Length; i++)
        {
            var contour = contours[i];
            if (contour.Length <= 2)
            {
                continue;
            }

            SKPoint[] minBox = GetMiniBox(contour, out float maxSide);
            if (float.IsNaN(maxSide) || maxSide < maxSideThresh)
            {
                continue;
            }

            // NOTE: Python's DBPostProcess.box_score_fast uses the mini-rect 4-point box.
            // Empirically the bundled PP-OCRv5 ONNX produces better boxes when scored
            // against the full contour ("slow" mode), switching to fast slightly
            // changes which low-confidence boxes pass, and downstream classification
            // can flip 180° on a few borderline crops. Keeping legacy contour scoring.
            double score = GetScore(contour, predImage);
            if (score < boxScoreThresh)
            {
                continue;
            }

            SKPoint[]? clipBox = Unclip(minBox, unClipRatio);
            if (clipBox is null)
            {
                continue;
            }

            ReadOnlySpan<SKPoint> clipMinBox = GetMiniBox(clipBox, out maxSide);
            if (float.IsNaN(maxSide) || maxSide < maxSideThresh + 2)
            {
                continue;
            }

            var finalPoints = new SKPointI[clipMinBox.Length];

            for (int j = 0; j < clipMinBox.Length; j++)
            {
                var item = clipMinBox[j];
                int x = (int)(item.X / s.ScaleWidth);
                int ptx = Math.Min(Math.Max(x, 0), s.SrcWidth);

                int y = (int)(item.Y / s.ScaleHeight);
                int pty = Math.Min(Math.Max(y, 0), s.SrcHeight);

                finalPoints[j] = new SKPointI(ptx, pty);
            }

            var textBox = new TextBox
            {
                Score = (float)score,
                BoxPoints = finalPoints
            };

            rsBoxes.Add(textBox);
        }

        return SortBoxesInReadingOrder(rsBoxes);
    }

    /// <summary>
    /// Stable top-to-bottom, then left-to-right ordering within each line.
    /// Matches PP-OCR <c>TextDetector.sorted_boxes</c>: boxes within 10 pixels of
    /// vertical proximity are considered the same line.
    /// </summary>
    private const int LineSortYThreshold = 10;

    private static List<TextBox> SortBoxesInReadingOrder(List<TextBox> boxes)
    {
        if (boxes.Count < 2)
        {
            return boxes;
        }

        // Stable sort by top-left Y.
        var ySorted = boxes
            .Select((b, i) => (box: b, originalIndex: i))
            .OrderBy(t => t.box.BoxPoints[0].Y)
            .ToList();

        // Assign line IDs based on adjacent Y gaps.
        var lineIds = new int[ySorted.Count];
        for (int i = 1; i < ySorted.Count; i++)
        {
            int dy = ySorted[i].box.BoxPoints[0].Y - ySorted[i - 1].box.BoxPoints[0].Y;
            lineIds[i] = dy >= LineSortYThreshold ? lineIds[i - 1] + 1 : lineIds[i - 1];
        }

        return ySorted
            .Select((t, i) => (t.box, line: lineIds[i]))
            .OrderBy(t => t.line)
            .ThenBy(t => t.box.BoxPoints[0].X)
            .Select(t => t.box)
            .ToList();
    }

    /// <summary>
    /// Gets the mini box.
    /// </summary>
    /// <param name="contours"></param>
    /// <param name="minEdgeSize"><see cref="float.NaN"/> if not valid.</param>
    private static SKPoint[] GetMiniBox(SKPoint[] contours, out float minEdgeSize)
    {
        SKPoint[] points = GeometryExtensions.MinimumAreaRectangle(contours);

        if (points.Length < 4)
        {
            minEdgeSize = float.NaN;
            return [];
        }

        GeometryExtensions.GetSize(points, out float width, out float height);
        minEdgeSize = MathF.Min(width, height);

        Array.Sort(points, CompareByX);

        int index1 = 0;
        int index2 = 1;
        int index3 = 2;
        int index4 = 3;

        if (points[1].Y > points[0].Y)
        {
            index1 = 0;
            index4 = 1;
        }
        else
        {
            index1 = 1;
            index4 = 0;
        }

        if (points[3].Y > points[2].Y)
        {
            index2 = 2;
            index3 = 3;
        }
        else
        {
            index2 = 3;
            index3 = 2;
        }

        return [points[index1], points[index2], points[index3], points[index4]];
    }

    public static int CompareByX(SKPoint left, SKPoint right)
    {
        if (left.X > right.X)
        {
            return 1;
        }

        if (left.X == right.X)
        {
            return 0;
        }

        return -1;
    }

    private static double GetScore(SKPoint[] contours, SKBitmap fMapMat)
    {
        short xmin = 9999;
        short xmax = 0;
        short ymin = 9999;
        short ymax = 0;

        try
        {
            foreach (SKPoint point in contours)
            {
                if (point.X < xmin)
                {
                    xmin = (short)point.X;
                }

                if (point.X > xmax)
                {
                    xmax = (short)point.X;
                }

                if (point.Y < ymin)
                {
                    ymin = (short)point.Y;
                }

                if (point.Y > ymax)
                {
                    ymax = (short)point.Y;
                }
            }

            // The cropped destBitmap shares the underlying pixel buffer with the source, so changes
            // to either will affect both unless you copy it.
            var roiBitmap = new SKBitmap();
            if (!fMapMat.ExtractSubset(roiBitmap, new SKRectI(xmin - 1, ymin - 1, xmax, ymax)))
            {
                roiBitmap.Dispose();
                throw new InvalidOperationException($"Could not extract subset for rect ({xmin - 1},{ymin - 1},{xmax},{ymax}) from {fMapMat.Width}x{fMapMat.Height} pred map.");
            }

            using (roiBitmap)
            {
                double sum = 0;
                int count = 0;

                int roiWidth = xmax - xmin + 1;
                int roiHeight = ymax - ymin + 1;

                var gray8 = new SKImageInfo()
                {
                    Height = roiHeight,
                    Width = roiWidth,
                    AlphaType = SKAlphaType.Opaque,
                    ColorType = SKColorType.Gray8
                };

                using (var mask = new SKBitmap(gray8))
                using (var canvas = new SKCanvas(mask))
                using (var maskPaint = new SKPaint())
                {
                    maskPaint.Color = SKColors.White;
                    maskPaint.Style = SKPaintStyle.Fill;

                    canvas.Clear(SKColors.Black);

                    using (var path = new SKPath())
                    {
                        SKPoint first = contours[0];
                        path.MoveTo(first.X - xmin, first.Y - ymin);
                        for (int p = 1; p < contours.Length; p++)
                        {
                            SKPoint point = contours[p];
                            path.LineTo(point.X - xmin, point.Y - ymin);
                        }

                        path.Close();

                        canvas.DrawPath(path, maskPaint);
                    }

                    //#if DEBUG
                    //  using (var fs = new FileStream($"mask_{Guid.NewGuid()}.png", FileMode.Create))
                    //  {
                    //      mask.Encode(fs, SKEncodedImageFormat.Png, 100);
                    //  }
                    //#endif

                    ReadOnlySpan<byte> maskSpan = mask.GetPixelSpan();
                    ReadOnlySpan<byte> roiSpan = roiBitmap.GetPixelSpan();
                    int roiRowBytes = roiBitmap.RowBytes;

                    // maskSpan is roiWidth*roiHeight (packed), but roiSpan uses the source stride (roiRowBytes,
                    // because of ExtractSubset()), so we must convert the flat mask index to (row, col) to get
                    // the correct roiSpan offset.
                    System.Diagnostics.Debug.Assert(maskSpan.Length == roiWidth * roiHeight);

                    for (int i = 0; i < maskSpan.Length; i++)
                    {
                        if (maskSpan[i] == 0)
                        {
                            continue;
                        }

                        int row = i / roiWidth;
                        int col = i % roiWidth;
                        sum += roiSpan[row * roiRowBytes + col];
                        count++;
                    }
                }

                if (count == 0)
                {
                    return 0;
                }

                return sum / count / byte.MaxValue;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(ex.Message + ex.StackTrace);
        }

        return 0;
    }

    private static SKPoint[]? Unclip(SKPoint[] box, float unclipRatio)
    {
        SKPoint[] points = GeometryExtensions.MinimumAreaRectangle(box);
        GeometryExtensions.GetSize(points, out float width, out float height);

        if (float.IsNaN(width) || float.IsNaN(height))
        {
            return null;
        }

        if (height < 1.001 && width < 1.001)
        {
            return null;
        }

        var theClipperPts = new Path64(box.Length);
        for (int i = 0; i < box.Length; ++i)
        {
            theClipperPts.Add(new Point64(box[i].X, box[i].Y));
        }

        float area = MathF.Abs(SignedPolygonArea(box));
        double length = LengthOfPoints(box);
        double distance = area * unclipRatio / length;

        var co = new ClipperOffset();
        co.AddPath(theClipperPts, JoinType.Round, EndType.Polygon);
        var solution = new Paths64();
        co.Execute(distance, solution);
        if (solution.Count == 0)
        {
            return null;
        }

        var unclipped = solution[0];

        var retPts = new SKPoint[unclipped.Count];
        for (int i = 0; i < unclipped.Count; ++i)
        {
            var ip = unclipped[i];
            retPts[i] = new SKPoint((int)ip.X, (int)ip.Y);
        }

        return retPts;
    }

    private static float SignedPolygonArea(SKPoint[] points)
    {
        // Get the areas.
        float area = 0;
        for (int i = 0; i < points.Length - 1; i++)
        {
            area +=
                (points[i + 1].X - points[i].X) *
                (points[i + 1].Y + points[i].Y) / 2;
        }

        area +=
            (points[0].X - points[points.Length - 1].X) *
            (points[0].Y + points[points.Length - 1].Y) / 2;

        return area;
    }

    private static double LengthOfPoints(SKPoint[] box)
    {
        double length = 0;

        SKPoint pt = box[0];
        double x0 = pt.X;
        double y0 = pt.Y;

        for (int idx = 1; idx < box.Length; idx++)
        {
            SKPoint pts = box[idx];
            double x1 = pts.X;
            double y1 = pts.Y;
            double dx = x1 - x0;
            double dy = y1 - y0;

            length += Math.Sqrt(dx * dx + dy * dy);

            x0 = x1;
            y0 = y1;
        }

        // Compute distance from last point to first point (closed loop)
        var dxL = pt.X - x0;
        var dyL = pt.Y - y0;
        length += Math.Sqrt(dxL * dxL + dyL * dyL);

        return length;
    }

    public void Dispose()
    {
        _dbNet.Dispose();
        _dilatePaint.Dispose();
        _dilateFilter.Dispose();
    }
}
