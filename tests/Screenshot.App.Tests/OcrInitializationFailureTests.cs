using System.IO;
using Microsoft.ML.OnnxRuntime;
using RapidOcrNet;
using Screenshot.App.Text;

namespace Screenshot.App.Tests;

public sealed class OcrInitializationFailureTests
{
    [Fact]
    public void InvalidModelReportsOriginalOnnxFailureEvenWhenPartialEngineDisposalThrows()
    {
        var directory = Directory.CreateTempSubdirectory("SnapCut-OcrInit-");
        try
        {
            var path = Path.Combine(directory.FullName, "invalid.onnx");
            File.WriteAllText(path, "not an ONNX model");
            var models = RapidOcrModelSet.PPOCRv6Small with { DetModelPath = path };
            var error = Assert.Throws<OnnxRuntimeException>(() => HighQualityOcrService.CreateEngine(models, 1));
            Assert.Contains("TextDetector.InitModel", error.StackTrace);
            Assert.Contains("NullReferenceException", error.Data[OcrFailureDetails.CleanupErrorKey] as string);
            var diagnostic = OcrFailureDetails.FormatException(error);
            Assert.StartsWith(error.ToString(), diagnostic);
            Assert.Contains("Secondary cleanup failure", diagnostic);
            var message = OcrFailureDetails.Describe(error);
            Assert.Contains("OnnxRuntimeException", message);
            Assert.DoesNotContain("NullReferenceException", message);
        }
        finally { directory.Delete(recursive: true); }
    }

    [Fact]
    public void MissingNativeDependencyShowsInnerCauseAndDoesNotBlameOldHardware()
    {
        var error = new TypeInitializationException("NativeMethods", new DllNotFoundException("onnxruntime.dll"));
        var message = OcrFailureDetails.Describe(error);
        Assert.Contains("DllNotFoundException", message);
        Assert.Contains("onnxruntime.dll", message);
        Assert.Contains("依赖", message);
        Assert.DoesNotContain("AVX", message);
    }

    [Fact]
    public void ErrorMessageIsBoundedButDiagnosticKeepsFullReason()
    {
        var exception = new InvalidOperationException(new string('x', 1000));
        Assert.True(OcrFailureDetails.Describe(exception).Length < 400);
        Assert.Contains(exception.Message, OcrFailureDetails.FormatException(exception));
    }
}
