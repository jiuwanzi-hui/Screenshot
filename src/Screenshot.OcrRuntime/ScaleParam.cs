// Apache-2.0 license
// Adapted from RapidAI / RapidOCR
// https://github.com/RapidAI/RapidOCR/blob/92aec2c1234597fa9c3c270efd2600c83feecd8d/dotnet/RapidOcrOnnxCs/OcrLib/ScaleParam.cs

using SkiaSharp;

namespace RapidOcrNet;

public sealed record ScaleParam
{
    public int SrcWidth { get; }

    public int SrcHeight { get; }

    public int DstWidth { get; }

    public int DstHeight { get; }

    public float ScaleWidth { get; }

    public float ScaleHeight { get; }

    public ScaleParam(int srcWidth, int srcHeight, int dstWidth, int dstHeight)
    {
        SrcWidth = srcWidth;
        SrcHeight = srcHeight;
        DstWidth = dstWidth;
        DstHeight = dstHeight;
        ScaleWidth = dstWidth / (float)srcWidth;
        ScaleHeight = dstHeight / (float)srcHeight;
    }

    public override string ToString()
    {
        return $"sw:{SrcWidth},sh:{SrcHeight},dw:{DstWidth},dh:{DstHeight},{ScaleWidth},{ScaleHeight}";
    }

    /// <summary>
    /// Legacy: treats <paramref name="dstSize"/> as a max-side cap, then rounds down
    /// to /32. (Python rounds to nearest /32, but empirically the bundled PP-OCRv5
    /// detector regresses on several inputs when fed nearest-rounded sizes, sticking
    /// with the legacy round-down.) Prefer <see cref="GetAdaptiveScaleParam"/> for the
    /// Python-style adaptive flow.
    /// </summary>
    public static ScaleParam GetScaleParam(SKBitmap src, int dstSize)
    {
        int srcWidth = src.Width;
        int dstWidth = src.Width;
        int srcHeight = src.Height;
        int dstHeight = src.Height;

        if (dstWidth > dstHeight)
        {
            float scale = dstSize / (float)dstWidth;
            dstWidth = dstSize;
            dstHeight = (int)(dstHeight * scale);
        }
        else
        {
            float scale = dstSize / (float)dstHeight;
            dstHeight = dstSize;
            dstWidth = (int)(dstWidth * scale);
        }

        if (dstWidth % 32 != 0)
        {
            dstWidth = (dstWidth / 32 - 1) * 32;
            dstWidth = Math.Max(dstWidth, 32);
        }

        if (dstHeight % 32 != 0)
        {
            dstHeight = (dstHeight / 32 - 1) * 32;
            dstHeight = Math.Max(dstHeight, 32);
        }

        return new ScaleParam(srcWidth, srcHeight, dstWidth, dstHeight);
    }

    /// <summary>
    /// PP-OCR-style detector resize matching Python rapidocr's default
    /// <c>limit_type="min"</c> with <c>limit_side_len=736</c>: scale **up** so the
    /// short side reaches <paramref name="limitSideLen"/>; images whose short side is
    /// already at or above the limit are left at native size. Dst dimensions are
    /// rounded to the **nearest** /32 (Python: <c>int(round(x/32) * 32)</c>).
    /// </summary>
    /// <param name="src">Source bitmap.</param>
    /// <param name="limitSideLen">Target short-side length. Default 736 matches Python's
    /// <c>config.yaml: Det.limit_side_len: 736</c>.</param>
    public static ScaleParam GetAdaptiveScaleParam(SKBitmap src, int limitSideLen = 736)
    {
        int srcWidth = src.Width;
        int srcHeight = src.Height;

        float ratio = 1.0f;
        int minWh = Math.Min(srcWidth, srcHeight);
        if (limitSideLen > 0 && minWh < limitSideLen)
        {
            ratio = limitSideLen / (float)minWh;
        }

        int dstWidth = (int)(srcWidth * ratio);
        int dstHeight = (int)(srcHeight * ratio);

        dstWidth = RoundToMultiple(dstWidth, 32);
        dstHeight = RoundToMultiple(dstHeight, 32);

        return new ScaleParam(srcWidth, srcHeight, dstWidth, dstHeight);
    }

    private static int RoundToMultiple(int value, int m)
    {
        int rounded = (int)Math.Round(value / (double)m) * m;
        return Math.Max(rounded, m);
    }
}