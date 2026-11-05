// Apache-2.0 license

using System.IO;

namespace RapidOcrNet;

/// <summary>
/// Describes a complete set of OCR models (detector, classifier, recognizer and the
/// recognizer's character dictionary) plus the detector's pixel-space normalization.
/// Use the static presets to pick between the bundled PP-OCRv5 latin models and the
/// PP-OCRv6 models, or build a custom set pointing at your own files.
/// </summary>
/// <remarks>
/// Preset paths are resolved relative to the working/output directory under
/// <c>models/&lt;version&gt;/</c>, matching how <see cref="RapidOcr"/> resolves its
/// default model constants. The PP-OCRv6 presets reuse the PP-OCRv5 classifier model,
/// because PP-OCRv6 ships no classifier of its own.
/// </remarks>
public sealed record RapidOcrModelSet
{
    /// <summary>Path to the detector (DBNet) ONNX model.</summary>
    public required string DetModelPath { get; init; }

    /// <summary>Path to the angle classifier ONNX model.</summary>
    public required string ClsModelPath { get; init; }

    /// <summary>Path to the recognizer (CRNN) ONNX model.</summary>
    public required string RecModelPath { get; init; }

    /// <summary>Path to the recognizer's character dictionary (keys) file.</summary>
    public required string KeysPath { get; init; }

    /// <summary>
    /// Detector normalization mean, in pixel space (e.g. 0.5 maps to 127.5).
    /// PP-OCRv5 uses ImageNet means; PP-OCRv6 uses 127.5.
    /// </summary>
    public required float[] DetMean { get; init; }

    /// <summary>
    /// Detector normalization std, in pixel space (NOT pre-inverted). PP-OCRv5 uses
    /// ImageNet stds; PP-OCRv6 uses 127.5.
    /// </summary>
    public required float[] DetStd { get; init; }

    // ImageNet normalization in pixel space, what the bundled PP-OCRv5 mobile detector expects.
    private static readonly float[] ImageNetMean = [0.485F * 255F, 0.456F * 255F, 0.406F * 255F];
    private static readonly float[] ImageNetStd = [0.229F * 255F, 0.224F * 255F, 0.225F * 255F];

    // PP-OCRv6 detector normalization: mean/std (0.5, 0.5, 0.5) in pixel space.
    private static readonly float[] HalfMean = [127.5F, 127.5F, 127.5F];
    private static readonly float[] HalfStd = [127.5F, 127.5F, 127.5F];

    private static string V5(string fileName) => Path.Combine(RapidOcr.ModelsFolderName, "v5", fileName);
    private static string V6(string fileName) => Path.Combine(RapidOcr.ModelsFolderName, "v6", fileName);

    private const string V5ClsModel = "ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx";

    /// <summary>The bundled PP-OCRv5 latin models (the library default).</summary>
    public static RapidOcrModelSet PPOCRv5Latin { get; } = new()
    {
        DetModelPath = V5("ch_PP-OCRv5_mobile_det.onnx"),
        ClsModelPath = V5(V5ClsModel),
        RecModelPath = V5("latin_PP-OCRv5_rec_mobile_infer.onnx"),
        KeysPath = V5("ppocrv5_latin_dict.txt"),
        DetMean = ImageNetMean,
        DetStd = ImageNetStd
    };

    /// <summary>PP-OCRv6 tiny models (smallest, fastest, lowest accuracy).</summary>
    public static RapidOcrModelSet PPOCRv6Tiny { get; } = new()
    {
        DetModelPath = V6("PP-OCRv6_det_tiny.onnx"),
        ClsModelPath = V5(V5ClsModel),
        RecModelPath = V6("PP-OCRv6_rec_tiny.onnx"),
        KeysPath = V6("ppocrv6_tiny_dict.txt"),
        DetMean = HalfMean,
        DetStd = HalfStd
    };

    /// <summary>PP-OCRv6 small models (balanced size/accuracy, the v6 default).</summary>
    public static RapidOcrModelSet PPOCRv6Small { get; } = new()
    {
        DetModelPath = V6("PP-OCRv6_det_small.onnx"),
        ClsModelPath = V5(V5ClsModel),
        RecModelPath = V6("PP-OCRv6_rec_small.onnx"),
        KeysPath = V6("ppocrv6_dict.txt"),
        DetMean = HalfMean,
        DetStd = HalfStd
    };

    /// <summary>PP-OCRv6 medium models (largest, most accurate, slowest).</summary>
    public static RapidOcrModelSet PPOCRv6Medium { get; } = new()
    {
        DetModelPath = V6("PP-OCRv6_det_medium.onnx"),
        ClsModelPath = V5(V5ClsModel),
        RecModelPath = V6("PP-OCRv6_rec_medium.onnx"),
        KeysPath = V6("ppocrv6_dict.txt"),
        DetMean = HalfMean,
        DetStd = HalfStd
    };
}
