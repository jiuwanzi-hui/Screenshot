// Apache-2.0 license
// Adapted from RapidAI / RapidOCR
// https://github.com/RapidAI/RapidOCR/blob/92aec2c1234597fa9c3c270efd2600c83feecd8d/dotnet/RapidOcrOnnxCs/OcrLib/AngleNet.cs

using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SkiaSharp;

namespace RapidOcrNet;

public sealed class TextClassifier : IDisposable
{
    // Legacy fallback geometry, used when the model declares dynamic H/W (the
    // PP-OCRv4 ch_ppocr_mobile_v2.0 cls exports input [-1,3,-1,-1]).
    private const int DefaultAngleDstWidth = 192;
    private const int DefaultAngleDstHeight = 48;
    private const int AngleCols = 2;

    private static readonly float[] MeanValues = [127.5F, 127.5F, 127.5F];
    private static readonly float[] NormValues = [1.0F / 127.5F, 1.0F / 127.5F, 1.0F / 127.5F];

    // Resolved from the loaded model's input metadata in InitModel. The PP-OCRv5
    // ch_PP-LCNet_x0_25_textline_ori cls exports a fixed input [-1,3,80,160], so we
    // must feed 80x160; older dynamic-shape cls models keep the 48x192 default.
    private int _angleDstWidth = DefaultAngleDstWidth;
    private int _angleDstHeight = DefaultAngleDstHeight;

    private InferenceSession _angleNet;
    private string _inputName;

    public void InitModel(string path, SessionOptions op)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Classifier model file does not exist: '{path}'.");
        }

        _angleNet = new InferenceSession(path, op);
        _inputName = _angleNet.InputMetadata.Keys.First();

        // NCHW input: dims[2] is height, dims[3] is width. A dimension of -1 means
        // dynamic, in which case we keep the legacy 48x192 default.
        int[] dims = _angleNet.InputMetadata[_inputName].Dimensions;
        if (dims is { Length: 4 })
        {
            if (dims[2] > 0)
            {
                _angleDstHeight = dims[2];
            }

            if (dims[3] > 0)
            {
                _angleDstWidth = dims[3];
            }
        }
    }

    public void InitModel(string path, int numThread)
    {
        using var sessionOptions = RapidOcr.GetDefaultSessionOptions(numThread);
        InitModel(path, sessionOptions);
    }

    public Angle[] GetAngles(SKBitmap[] partImgs, bool doAngle, bool mostAngle, bool preserveAspectRatio = false)
    {
        var angles = new Angle[partImgs.Length];
        if (doAngle)
        {
            for (int i = 0; i < partImgs.Length; i++)
            {
                angles[i] = GetAngle(partImgs[i], preserveAspectRatio);
            }

            // Most Possible AngleIndex
            if (mostAngle)
            {
                int sum = 0;
                foreach (var a in angles)
                {
                    sum += a.Index;
                }
                double halfPercent = angles.Length / 2.0f;

                int mostAngleIndex = sum < halfPercent ? 0 : 1; // All angles set to 0 or 1
                System.Diagnostics.Debug.WriteLine($"Set All Angle to mostAngleIndex({mostAngleIndex})");
                foreach (var angle in angles)
                {
                    angle.Index = mostAngleIndex;
                }
            }
        }
        else
        {
            for (int i = 0; i < partImgs.Length; i++)
            {
                angles[i] = new Angle
                {
                    Index = -1,
                    Score = 0F
                };
            }
        }

        return angles;
    }

    public Angle GetAngle(SKBitmap src) => GetAngle(src, preserveAspectRatio: false);

    public Angle GetAngle(SKBitmap src, bool preserveAspectRatio)
    {
        var sw = ValueStopwatch.StartNew();
        Tensor<float> inputTensors;

        if (preserveAspectRatio)
        {
            // PP-OCR cls preprocessing (Python ch_ppocr_cls/main.py:83-106):
            //   1. resize preserving aspect to (resized_w, _angleDstHeight) where
            //      resized_w = min(_angleDstWidth, ceil(_angleDstHeight * w/h))
            //   2. zero-pad in normalized space (right side stays 0 in the [-1,1] tensor)
            //
            // In raw pixel space, "normalized 0" corresponds to midgray (127.5). So we
            // clear the canvas to midgray BEFORE drawing the resized strip, then run
            // the standard (pixel - 127.5) / 127.5 normalization on the whole image.
            float ratio = src.Width / (float)src.Height;
            int resizedW = Math.Min(_angleDstWidth, (int)Math.Ceiling(_angleDstHeight * ratio));
            resizedW = Math.Max(resizedW, 1);

            var angleInfo = new SKImageInfo(_angleDstWidth, _angleDstHeight, SKColorType.Bgra8888, SKAlphaType.Opaque);
            using (var resized = src.Resize(new SKSizeI(resizedW, _angleDstHeight), OcrUtils.NetworkSampling))
            using (var angleImg = new SKBitmap(angleInfo))
            {
                using (var canvas = new SKCanvas(angleImg))
                {
                    canvas.Clear(new SKColor(128, 128, 128));
                    canvas.DrawBitmap(resized, 0, 0);
                }
                inputTensors = OcrUtils.SubtractMeanNormalize(angleImg, MeanValues, NormValues);
            }
        }
        else
        {
            // Legacy: non-uniform stretch to (_angleDstWidth, _angleDstHeight) with
            // Mitchell cubic.
            using (var angleImg = src.Resize(new SKSizeI(_angleDstWidth, _angleDstHeight), new SKSamplingOptions(SKCubicResampler.Mitchell)))
            {
                inputTensors = OcrUtils.SubtractMeanNormalize(angleImg, MeanValues, NormValues);
            }
        }

        IReadOnlyCollection<NamedOnnxValue> inputs =
        [
            NamedOnnxValue.CreateFromTensor(_inputName, inputTensors)
        ];

        try
        {
            using (IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results = _angleNet.Run(inputs))
            {
                var outputTensor = results[0];

                ReadOnlySpan<float> outputData;
                if (outputTensor.AsTensor<float>() is DenseTensor<float> dt)
                {
                    outputData = dt.Buffer.Span;
                }
                else
                {
                    outputData = outputTensor.AsEnumerable<float>().ToArray();
                }

                var angle = ScoreToAngle(outputData, AngleCols);
                angle.Time = (float)sw.ElapsedMilliseconds;
                return angle;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(ex.Message + ex.StackTrace);
            //throw;
        }

        return new Angle() { Time = (float)sw.ElapsedMilliseconds };
    }

    private static Angle ScoreToAngle(ReadOnlySpan<float> srcData, int angleColumns)
    {
        int angleIndex = 0;
        float maxValue = srcData[0];

        for (int i = 1; i < angleColumns; ++i)
        {
            float current = srcData[i];
            if (current > maxValue)
            {
                angleIndex = i;
                maxValue = current;
            }
        }

        return new Angle
        {
            Index = angleIndex,
            Score = maxValue
        };
    }

    public void Dispose()
    {
        _angleNet.Dispose();
    }
}
