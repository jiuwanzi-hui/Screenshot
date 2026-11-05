// Apache-2.0 license
// Adapted from RapidAI / RapidOCR
// https://github.com/RapidAI/RapidOCR/blob/92aec2c1234597fa9c3c270efd2600c83feecd8d/dotnet/RapidOcrOnnxCs/OcrLib/OcrUtils.cs

using Microsoft.ML.OnnxRuntime.Tensors;
using SkiaSharp;

namespace RapidOcrNet;

/// <summary>
/// Geometry bookkeeping recorded while cropping a detection quad out of the source image.
/// Used to map word-box rectangles in recognized-image coords back to original source coords.
/// </summary>
internal readonly struct CropContext
{
    public readonly int Left;
    public readonly int Top;

    /// <summary>Width of the rectified strip (partImg) before any 90° pre-rotation.</summary>
    public readonly int PartImgWidth;

    /// <summary>Height of the rectified strip (partImg) before any 90° pre-rotation.</summary>
    public readonly int PartImgHeight;

    /// <summary>Forward perspective matrix mapping imgCrop coords → partImg coords.</summary>
    public readonly SKMatrix PerspectiveMatrix;

    public readonly bool HasPerspective;

    /// <summary>True if a 90° rotation was applied; CounterClockwise records its direction.</summary>
    public readonly bool Rotated90;
    public readonly bool CounterClockwise;

    public CropContext(int left, int top, int partImgWidth, int partImgHeight,
        SKMatrix perspectiveMatrix, bool hasPerspective, bool rotated90, bool counterClockwise = false)
    {
        Left = left;
        Top = top;
        PartImgWidth = partImgWidth;
        PartImgHeight = partImgHeight;
        PerspectiveMatrix = perspectiveMatrix;
        HasPerspective = hasPerspective;
        Rotated90 = rotated90;
        CounterClockwise = counterClockwise;
    }
}

internal static class OcrUtils
{
    /// <summary>
    /// Sampling used for detector / classifier / recognizer network inputs. Python
    /// uses cv2.INTER_LINEAR; bilinear sampling would be the natural analogue, but
    /// empirically the bundled PP-OCRv5 ONNX models behave better when fed with the
    /// same Mitchell cubic resampler the original C# port used. Switching to bilinear
    /// shifts pixel values enough to flip 180° on borderline classifier inputs.
    /// </summary>
    public static readonly SKSamplingOptions NetworkSampling = new SKSamplingOptions(SKCubicResampler.Mitchell);

    /// <summary>
    /// Sampling used for the rotate-crop perspective warp. Python uses
    /// cv2.INTER_CUBIC; Mitchell is what the bundled ONNX models were tuned with.
    /// </summary>
    public static readonly SKSamplingOptions WarpSampling = new SKSamplingOptions(SKCubicResampler.Mitchell);

    public static Tensor<float> SubtractMeanNormalize(SKBitmap src, float[] meanVals, float[] normVals)
    {
        const int batchSize = 1;

        int cols = src.Width;
        int rows = src.Height;
        int channels = src.BytesPerPixel;
        int rowBytes = src.RowBytes; // Use actual row stride (may include padding)

        const int expChannels = 3; // Size of meanVals, we ignore alpha channel

        var inputTensor = new DenseTensor<float>([batchSize, expChannels, rows, cols]);
        // Write planar buffers directly. Tensor's params-indexer otherwise creates
        // coordinate arrays for every channel of every pixel in this hot loop.
        var planeSize = checked(rows * cols);
        var target = inputTensor.Buffer.Span;
        var blue = target[..planeSize];
        var green = target.Slice(planeSize, planeSize);
        var red = target.Slice(planeSize * 2, planeSize);

        ReadOnlySpan<byte> span = src.GetPixelSpan();

        if (src.Info.ColorType == SKColorType.Gray8)
        {
            float mean0 = meanVals[0], mean1 = meanVals[1], mean2 = meanVals[2];
            float norm0 = normVals[0], norm1 = normVals[1], norm2 = normVals[2];
            for (int r = 0; r < rows; ++r)
            {
                int rowBase = r * rowBytes;
                for (int c = 0; c < cols; ++c)
                {
                    byte value = span[rowBase + c];
                    int destination = r * cols + c;
                    blue[destination] = (value - mean0) * norm0;
                    green[destination] = (value - mean1) * norm1;
                    red[destination] = (value - mean2) * norm2;
                }
            }
        }
        else if (src.Info.ColorType == SKColorType.Bgra8888)
        {
            float mean0 = meanVals[0], mean1 = meanVals[1], mean2 = meanVals[2];
            float norm0 = normVals[0], norm1 = normVals[1], norm2 = normVals[2];
            for (int r = 0; r < rows; ++r)
            {
                int rowBase = r * rowBytes;
                for (int c = 0; c < cols; ++c)
                {
                    int pixelBase = rowBase + c * channels;
                    int destination = r * cols + c;
                    blue[destination] = (span[pixelBase] - mean0) * norm0;
                    green[destination] = (span[pixelBase + 1] - mean1) * norm1;
                    red[destination] = (span[pixelBase + 2] - mean2) * norm2;
                }
            }
        }
        else
        {
            throw new ArgumentException($"This image needs to be '{SKColorType.Bgra8888}' or '{SKColorType.Gray8}', but got '{src.Info.ColorType}'.");
        }

        return inputTensor;
    }

    /// <summary>
    /// PP-OCR-style image bounding: first downscale so the longer side is ≤
    /// <paramref name="maxSideLen"/> (Python <c>reduce_max_side</c>), then upscale if
    /// the shorter side is below <paramref name="minSideLen"/> (Python
    /// <c>increase_min_side</c>). Dst dimensions are rounded to the nearest /32.
    /// Returns the source unchanged if both bounds are non-positive or already met.
    /// </summary>
    public static SKBitmap ResizeImageWithinBounds(SKBitmap src, int minSideLen, int maxSideLen, out bool owned)
    {
        int srcW = src.Width;
        int srcH = src.Height;

        int dstW = srcW;
        int dstH = srcH;

        // Step 1: reduce_max_side
        int maxV = Math.Max(dstW, dstH);
        if (maxSideLen > 0 && maxV > maxSideLen)
        {
            float ratio = maxSideLen / (float)maxV;
            dstW = (int)(dstW * ratio);
            dstH = (int)(dstH * ratio);
        }

        // Step 2: increase_min_side
        int minV = Math.Min(dstW, dstH);
        if (minSideLen > 0 && minV < minSideLen)
        {
            float ratio = minSideLen / (float)minV;
            dstW = (int)(dstW * ratio);
            dstH = (int)(dstH * ratio);
        }

        // Round to nearest /32 to satisfy the detector's stride.
        dstW = RoundToMultiple32(dstW);
        dstH = RoundToMultiple32(dstH);

        if (dstW == srcW && dstH == srcH)
        {
            owned = false;
            return src;
        }

        var resized = src.Resize(new SKSizeI(dstW, dstH), NetworkSampling);
        owned = true;
        return resized;
    }

    private static int RoundToMultiple32(int value)
    {
        int rounded = (int)Math.Round(value / 32.0) * 32;
        return Math.Max(rounded, 32);
    }

    /// <summary>
    /// Apply Python-style conditional vertical letterbox: only adds top+bottom padding
    /// when the input is too wide (w/h &gt; widthHeightRatio) or too short (h &lt; minHeight).
    /// Returns the padded bitmap and the top-padding amount (left-padding is always 0).
    /// </summary>
    public static SKBitmap ApplyVerticalLetterbox(SKBitmap src, float widthHeightRatio, int minHeight, out int paddingTop)
    {
        paddingTop = 0;

        int w = src.Width;
        int h = src.Height;

        bool useLimitRatio = widthHeightRatio > 0 && (w / (float)h > widthHeightRatio);
        bool tooShort = h <= minHeight;
        if (!useLimitRatio && !tooShort)
        {
            return src;
        }

        // Python: new_h = max(int(w / wh_ratio), min_height) * 2; padding_h = abs(new_h - h)/2
        int referenceWidth = widthHeightRatio > 0 ? (int)(w / widthHeightRatio) : minHeight;
        int newH = Math.Max(referenceWidth, minHeight) * 2;
        int padH = Math.Abs(newH - h) / 2;
        if (padH <= 0)
        {
            return src;
        }

        var info = src.Info;
        info.Height = h + 2 * padH;

        var padded = new SKBitmap(info);
        using (var canvas = new SKCanvas(padded))
        using (var image = SKImage.FromBitmap(src))
        {
            // White matches the rest of the C# pipeline's "light background" assumption
            // (the bundled PP-OCRv5 ONNX is tuned for it). Python uses BLACK here; the
            // model bundled with rapidocr-python was trained accordingly.
            canvas.Clear(SKColors.White);
            canvas.DrawImage(image, 0, padH, WarpSampling);
        }

        paddingTop = padH;
        return padded;
    }

    public static SKBitmap MakePadding(SKBitmap src, int padding)
    {
        if (padding <= 0)
        {
            return src;
        }

        SKImageInfo info = src.Info;

        info.Width += 2 * padding;
        info.Height += 2 * padding;

        SKBitmap newBmp = new SKBitmap(info);
        using (var canvas = new SKCanvas(newBmp))
        using (var image = SKImage.FromBitmap(src))
        {
            canvas.Clear(SKColors.White);
            canvas.DrawImage(image, padding, padding, WarpSampling);
        }

//#if DEBUG
//        using (var fs = new FileStream($"Padding_{Guid.NewGuid()}.png", FileMode.Create))
//        {
//            newBmp.Encode(fs, SKEncodedImageFormat.Png, 100);
//        }
//#endif

        return newBmp;
    }

    public static int GetThickness(SKBitmap boxImg)
    {
        int minSize = boxImg.Width > boxImg.Height ? boxImg.Height : boxImg.Width;
        return minSize / 1000 + 2;
    }

    /// <summary>
    /// Crop and rectify every detection quad in <paramref name="textBoxes"/>. Returns an
    /// eagerly-allocated array; if any single crop throws partway through, all already
    /// allocated bitmaps are disposed before the exception propagates.
    /// </summary>
    public static SKBitmap[] GetPartImages(SKBitmap src, IReadOnlyList<TextBox>? textBoxes, bool counterClockwise = false)
    {
        if (textBoxes is null || textBoxes.Count == 0)
        {
            return [];
        }

        var images = new SKBitmap[textBoxes.Count];
        int produced = 0;
        try
        {
            for (int i = 0; i < textBoxes.Count; ++i)
            {
                images[i] = GetRotateCropImage(src, textBoxes[i].BoxPoints, out _, counterClockwise);
                produced = i + 1;
            }

            return images;
        }
        catch
        {
            for (int i = 0; i < produced; i++)
            {
                images[i].Dispose();
            }
            throw;
        }
    }

    /// <summary>
    /// Like <see cref="GetPartImages"/> but also records per-crop <see cref="CropContext"/>
    /// bookkeeping for later word-box inverse mapping. Same exception-safety contract.
    /// </summary>
    public static (SKBitmap[] PartImages, CropContext[] Contexts) GetPartImagesWithContext(SKBitmap src,
        IReadOnlyList<TextBox>? textBoxes, bool counterClockwise = false)
    {
        if (textBoxes is null || textBoxes.Count == 0)
        {
            return ([], []);
        }

        var images = new SKBitmap[textBoxes.Count];
        var contexts = new CropContext[textBoxes.Count];
        int produced = 0;
        try
        {
            for (int i = 0; i < textBoxes.Count; ++i)
            {
                images[i] = GetRotateCropImage(src, textBoxes[i].BoxPoints, out contexts[i], counterClockwise);
                produced = i + 1;
            }

            return (images, contexts);
        }
        catch
        {
            for (int i = 0; i < produced; i++)
            {
                images[i].Dispose();
            }
            throw;
        }
    }

    public static SKMatrix GetPerspectiveTransform(in SKPointI topLeft, in SKPointI topRight, in SKPointI botRight, in SKPointI botLeft,
        float width, float height)
    {
        // https://stackoverflow.com/questions/48416118/perspective-transform-in-skia

        float x1 = topLeft.X;
        float y1 = topLeft.Y;
        float x2 = topRight.X;
        float y2 = topRight.Y;
        float x3 = botRight.X;
        float y3 = botRight.Y;
        float x4 = botLeft.X;
        float y4 = botLeft.Y;

        float w = width;
        float h = height;

        float scaleX = (y1 * x2 * x4 - x1 * y2 * x4 + x1 * y3 * x4 - x2 * y3 * x4 - y1 * x2 * x3 + x1 * y2 * x3 - x1 * y4 * x3 + x2 * y4 * x3) / (x2 * y3 * w + y2 * x4 * w - y3 * x4 * w - x2 * y4 * w - y2 * w * x3 + y4 * w * x3);
        float skewX = (-x1 * x2 * y3 - y1 * x2 * x4 + x2 * y3 * x4 + x1 * x2 * y4 + x1 * y2 * x3 + y1 * x4 * x3 - y2 * x4 * x3 - x1 * y4 * x3) / (x2 * y3 * h + y2 * x4 * h - y3 * x4 * h - x2 * y4 * h - y2 * h * x3 + y4 * h * x3);
        float transX = x1;
        float skewY = (-y1 * x2 * y3 + x1 * y2 * y3 + y1 * y3 * x4 - y2 * y3 * x4 + y1 * x2 * y4 - x1 * y2 * y4 - y1 * y4 * x3 + y2 * y4 * x3) / (x2 * y3 * w + y2 * x4 * w - y3 * x4 * w - x2 * y4 * w - y2 * w * x3 + y4 * w * x3);
        float scaleY = (-y1 * x2 * y3 - y1 * y2 * x4 + y1 * y3 * x4 + x1 * y2 * y4 - x1 * y3 * y4 + x2 * y3 * y4 + y1 * y2 * x3 - y2 * y4 * x3) / (x2 * y3 * h + y2 * x4 * h - y3 * x4 * h - x2 * y4 * h - y2 * h * x3 + y4 * h * x3);
        float transY = y1;
        float persp0 = (x1 * y3 - x2 * y3 + y1 * x4 - y2 * x4 - x1 * y4 + x2 * y4 - y1 * x3 + y2 * x3) / (x2 * y3 * w + y2 * x4 * w - y3 * x4 * w - x2 * y4 * w - y2 * w * x3 + y4 * w * x3);
        float persp1 = (-y1 * x2 + x1 * y2 - x1 * y3 - y2 * x4 + y3 * x4 + x2 * y4 + y1 * x3 - y4 * x3) / (x2 * y3 * h + y2 * x4 * h - y3 * x4 * h - x2 * y4 * h - y2 * h * x3 + y4 * h * x3);
        float persp2 = 1;

        var persp = new SKMatrix(scaleX, skewX, transX, skewY, scaleY, transY, persp0, persp1, persp2);

        return persp.TryInvert(out SKMatrix perspInv) ? perspInv : SKMatrix.Identity; // TODO - Check what's best to return when not inv
    }

    public static SKBitmap GetRotateCropImage(SKBitmap src, SKPointI[] box)
    {
        return GetRotateCropImage(src, box, out _);
    }

    public static SKBitmap GetRotateCropImage(SKBitmap src, SKPointI[] box, out CropContext context, bool counterClockwise = false)
    {
        System.Diagnostics.Debug.Assert(box.Length == 4);

        SKPointI b0 = box[0];
        SKPointI b1 = box[1];
        SKPointI b2 = box[2];
        SKPointI b3 = box[3];

        // NOTE: must be independent `if`s (not `if/else if`) — a monotonically-decreasing
        // sequence would otherwise update `left` every iteration and never touch `right`,
        // leaving it at int.MinValue and producing a degenerate SKRectI below.
        ReadOnlySpan<int> collectX = stackalloc int[] { b0.X, b1.X, b2.X, b3.X };
        int left = int.MaxValue;
        int right = int.MinValue;
        foreach (var v in collectX)
        {
            if (v < left)
            {
                left = v;
            }

            if (v > right)
            {
                right = v;
            }
        }

        ReadOnlySpan<int> collectY = stackalloc int[] { b0.Y, b1.Y, b2.Y, b3.Y };
        int top = int.MaxValue;
        int bottom = int.MinValue;
        foreach (var v in collectY)
        {
            if (v < top)
            {
                top = v;
            }

            if (v > bottom)
            {
                bottom = v;
            }
        }

        SKRectI rect = new SKRectI(left, top, right, bottom);

        var info = src.Info;
        info.Width = rect.Width;
        info.Height = rect.Height;

        SKBitmap imgCrop = new SKBitmap(info);
        if (!src.ExtractSubset(imgCrop, rect))
        {
            imgCrop.Dispose();
            throw new InvalidOperationException($"Could not extract image subset for rect {rect} from {src.Width}x{src.Height} source.");
        }

        ref SKPointI p0 = ref b0;
        p0.X -= left;
        p0.Y -= top;

        ref SKPointI p1 = ref b1;
        p1.X -= left;
        p1.Y -= top;

        ref SKPointI p2 = ref b2;
        p2.X -= left;
        p2.Y -= top;

        ref SKPointI p3 = ref b3;
        p3.X -= left;
        p3.Y -= top;

        int imgCropWidth = (int)Math.Sqrt((p0.X - p1.X) * (p0.X - p1.X) + (p0.Y - p1.Y) * (p0.Y - p1.Y));
        int imgCropHeight = (int)Math.Sqrt((p0.X - p3.X) * (p0.X - p3.X) + (p0.Y - p3.Y) * (p0.Y - p3.Y));

        var m = GetPerspectiveTransform(in p0, in p1, in p2, in p3, imgCropWidth, imgCropHeight);

        if (m.IsIdentity)
        {
            bool rotated = imgCrop.Height >= imgCrop.Width * 1.5;
            context = new CropContext(
                left, top,
                imgCrop.Width, imgCrop.Height,
                SKMatrix.Identity, hasPerspective: false,
                rotated90: rotated, counterClockwise: counterClockwise);

            if (rotated)
            {
                var rotated90 = counterClockwise ? BitmapRotateCounterClockWise90(imgCrop) : BitmapRotateClockWise90(imgCrop);
                imgCrop.Dispose();
                return rotated90;
            }

            return imgCrop;
        }

        var info2 = imgCrop.Info;
        info2.Width = imgCropWidth;
        info2.Height = imgCropHeight;

        var partImg = new SKBitmap(info2);
        using (var canvas = new SKCanvas(partImg))
        using (var image = SKImage.FromBitmap(imgCrop))
        {
            canvas.SetMatrix(m);
            canvas.DrawImage(image, 0, 0, WarpSampling);
            canvas.Restore();
        }
        imgCrop.Dispose();

        bool rotated90Flag = partImg.Height >= partImg.Width * 1.5;
        context = new CropContext(
            left, top,
            partImg.Width, partImg.Height,
            m, hasPerspective: true,
            rotated90: rotated90Flag, counterClockwise: counterClockwise);

        if (rotated90Flag)
        {
            var rotated90 = counterClockwise ? BitmapRotateCounterClockWise90(partImg) : BitmapRotateClockWise90(partImg);
            partImg.Dispose();
            return rotated90;
        }

        return partImg;
    }

    public static SKBitmap BitmapRotateClockWise180(SKBitmap src)
    {
        var rotated = new SKBitmap(src.Info);

        using (var canvas = new SKCanvas(rotated))
        using (var image = SKImage.FromBitmap(src))
        {
            canvas.Translate(rotated.Width, rotated.Height);
            canvas.RotateDegrees(180);
            canvas.DrawImage(image, 0, 0, WarpSampling);
            canvas.Restore();
        }

        return rotated;
    }

    public static SKBitmap BitmapRotateClockWise90(SKBitmap src)
    {
        var info = src.Info;
        (info.Width, info.Height) = (info.Height, info.Width);

        var rotated = new SKBitmap(info);

        using (var canvas = new SKCanvas(rotated))
        using (var image = SKImage.FromBitmap(src))
        {
            canvas.Translate(rotated.Width, 0);
            canvas.RotateDegrees(90);
            canvas.DrawImage(image, 0, 0, WarpSampling);
            canvas.Restore();
        }

        return rotated;
    }

    /// <summary>
    /// Counter-clockwise 90° rotation matching <c>numpy.rot90</c> (k=1, default axes).
    /// Used as the pre-recognition rotation for tall (vertical) crops, so that the
    /// recognizer reads top-to-bottom characters in the same order as Python.
    /// </summary>
    public static SKBitmap BitmapRotateCounterClockWise90(SKBitmap src)
    {
        var info = src.Info;
        (info.Width, info.Height) = (info.Height, info.Width);

        var rotated = new SKBitmap(info);

        using (var canvas = new SKCanvas(rotated))
        using (var image = SKImage.FromBitmap(src))
        {
            canvas.Translate(0, rotated.Height);
            canvas.RotateDegrees(-90);
            canvas.DrawImage(image, 0, 0, WarpSampling);
            canvas.Restore();
        }

        return rotated;
    }
}
