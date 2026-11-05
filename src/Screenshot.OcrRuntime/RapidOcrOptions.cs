// Apache-2.0 license

namespace RapidOcrNet;

public sealed record RapidOcrOptions
{
    /// <summary>
    /// Default options tuned for the PP-OCRv5 ONNX models bundled with this repo.
    /// </summary>
    public static readonly RapidOcrOptions Default = new RapidOcrOptions()
    {
        Padding = 50,
        ImgResize = 1024,             // > 0 = legacy max-side cap (best fit for bundled v5 model)
        LimitSideLen = 736,           // used when ImgResize == 0 (Python-style adaptive)
        MaxSideLen = 2000,
        MinSideLen = 30,
        WidthHeightRatio = -1f,
        MinHeight = 30,
        TextScore = 0.5f,
        ClsThresh = 0.9f,
        ClsPreserveAspectRatio = true,// aspect-preserving + midgray pad
        BoxScoreThresh = 0.5f,
        BoxThresh = 0.3f,
        UnClipRatio = 1.6f,
        DoAngle = true,
        MostAngle = false,
        ReturnWordBox = false,
        ReturnSingleCharBox = false
    };

    /// <summary>
    /// Options matching the Python <c>rapidocr</c> reference pipeline: no white border,
    /// short-side adaptive resize, vertical letterbox for tall-thin inputs, and a 0.5
    /// average-char-score threshold for filtering low-confidence recognitions.
    /// </summary>
    public static readonly RapidOcrOptions PythonCompat = new RapidOcrOptions()
    {
        Padding = 0,
        ImgResize = 0,                  // 0 = Python-style adaptive short-side resize
        LimitSideLen = 736,             // Det.limit_side_len
        MaxSideLen = 2000,
        MinSideLen = 30,
        WidthHeightRatio = 8f,
        MinHeight = 30,
        TextScore = 0.5f,
        ClsThresh = 0.9f,
        ClsPreserveAspectRatio = true,  // aspect-preserving + midgray pad
        BoxScoreThresh = 0.5f,
        BoxThresh = 0.3f,
        UnClipRatio = 1.6f,
        DoAngle = true,
        MostAngle = false,
        ReturnWordBox = false,
        ReturnSingleCharBox = false
    };

    /// <summary>
    /// Recommended options for the PP-OCRv6 models (<see cref="RapidOcrModelSet.PPOCRv6Small"/> etc.).
    /// </summary>
    /// <remarks>
    /// These are the <see cref="PythonCompat"/> settings: no white border and a short-side
    /// adaptive resize to <see cref="LimitSideLen"/> (736), exactly matching the
    /// <c>limit_type="min"</c> detector preprocessing the standard PP-OCRv6 ONNX models were
    /// exported for. Do <b>not</b> use <see cref="Default"/> with v6 — its 1024 long-side cap
    /// and 50&#160;px white border are tuned for the bundled v5 model and starve the v6
    /// detector of resolution on small images (missed or garbled boxes).
    /// </remarks>
    public static readonly RapidOcrOptions PPOCRv6 = PythonCompat with { RotateTallCropsCounterClockwise = true, PadRecognitionInput = true };

    /// <summary>PPv6 recognition shape: at least 320 pixels wide, aligned to 32, with normalized zero padding.</summary>
    public bool PadRecognitionInput { get; init; }

    /// <summary>
    /// Optional extra all-sides padding applied to the source image before detection.
    /// Default 0 (Python doesn't do this). Set to e.g. 50 to restore legacy behavior for
    /// images with text running close to the edge.
    /// </summary>
    public int Padding { get; init; }

    /// <summary>
    /// Legacy max-side resize cap for the detector. When &gt; 0 it overrides the
    /// Python-style adaptive resize and caps the longer side at this value (rounded
    /// down to /32). Default 0 = use <see cref="LimitSideLen"/> with limit_type="min".
    /// </summary>
    public int ImgResize { get; init; }

    /// <summary>
    /// Target short-side length for the detector when <see cref="ImgResize"/> is 0.
    /// Matches Python's <c>Det.limit_side_len</c> (default 736). The detector input is
    /// upscaled so its short side reaches this; images whose short side is already at
    /// or above the limit are left at native size.
    /// </summary>
    public int LimitSideLen { get; init; }

    /// <summary>Upper bound on the longer side of the source image (Python <c>max_side_len</c>).</summary>
    public int MaxSideLen { get; init; }

    /// <summary>Lower bound on the shorter side of the source image (Python <c>min_side_len</c>).</summary>
    public int MinSideLen { get; init; }

    /// <summary>
    /// Width-to-height ratio above which a vertical letterbox is added (Python
    /// <c>width_height_ratio</c>, default 8). Use -1 to disable.
    /// </summary>
    public float WidthHeightRatio { get; init; }

    /// <summary>Min image height below which a vertical letterbox is added (Python <c>min_height</c>).</summary>
    public int MinHeight { get; init; }

    /// <summary>Drop blocks whose average char score is below this (Python <c>text_score</c>).</summary>
    public float TextScore { get; init; }

    /// <summary>
    /// Minimum classifier score required to actually apply a 180° flip (Python
    /// <c>cls_thresh</c>, default 0.9). Below this, the angle prediction is treated
    /// as not-confident-enough and the crop is left in its original orientation.
    /// </summary>
    public float ClsThresh { get; init; }

    /// <summary>
    /// When true, the classifier preprocesses crops the way Python rapidocr does:
    /// resize preserving aspect ratio to (resized_w, cls input height) and pad the
    /// remainder with midgray (equivalent to Python's "zero-pad after normalization").
    /// The cls input geometry is read from the loaded model (PP-OCRv5
    /// ch_PP-LCNet_x0_25_textline_ori is 80×160; legacy dynamic cls models fall back
    /// to 48×192). When false (default), a non-uniform stretch to that geometry is used.
    /// </summary>
    public bool ClsPreserveAspectRatio { get; init; }

    public float BoxScoreThresh { get; init; }
    public float BoxThresh { get; init; }
    public float UnClipRatio { get; init; }
    public bool DoAngle { get; init; }
    public bool MostAngle { get; init; }

    /// <summary>PP-OCRv6 follows Paddle's counter-clockwise tall-crop orientation.</summary>
    public bool RotateTallCropsCounterClockwise { get; init; }

    /// <summary>
    /// When true, each <see cref="TextBlock"/> exposes per-word polygons in
    /// <see cref="TextBlock.WordResults"/>. For Latin/numeric lines, one polygon per
    /// whitespace-separated word; for lines containing CJK characters, one polygon per character.
    /// </summary>
    public bool ReturnWordBox { get; init; }

    /// <summary>
    /// When true together with <see cref="ReturnWordBox"/>, Latin/numeric lines are also
    /// emitted at single-character granularity rather than per-word.
    /// </summary>
    public bool ReturnSingleCharBox { get; init; }
}
