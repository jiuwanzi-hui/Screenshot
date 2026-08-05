using System.Drawing.Imaging;
using RapidOcrNet;
using Screenshot.App.Capture;
using Screenshot.App.Core;
using SkiaSharp;
using System.IO;

namespace Screenshot.App.Text;

public static class HighQualityOcrService
{
    private static readonly TimeSpan EngineIdleTimeout = TimeSpan.FromSeconds(30);
    private static readonly object EngineLifecycleLock = new();
    private static readonly SemaphoreSlim EngineLock = new(1, 1);
    private static readonly System.Threading.Timer EngineUnloadTimer = new(
        _ => TryUnloadIdleEngine(),
        state: null,
        Timeout.InfiniteTimeSpan,
        Timeout.InfiniteTimeSpan);
    private static RapidOcr? _engine;
    private static string? _loadedDirectory;
    private static int _activeOperations;

    public static async Task<OcrRecognitionResult> RecognizeAsync(
        CapturedImage capturedImage,
        HighQualityOcrModelManager? modelManager = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(capturedImage);
        modelManager ??= HighQualityOcrModelManager.Shared;
        if (!modelManager.GetStatus().IsInstalled)
        {
            return OcrRecognitionResult.Failure(
                "高质量识别模型尚未下载，请到“内容识别”设置中安装。");
        }

        BeginEngineUse();
        var engineLockAcquired = false;
        try
        {
            await EngineLock.WaitAsync(cancellationToken);
            engineLockAcquired = true;
            EnsureEngine(modelManager);
            return await Task.Run(
                () => RecognizeCore(capturedImage, cancellationToken),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return OcrRecognitionResult.Failure("文字识别已取消。");
        }
        catch (Exception)
        {
            return OcrRecognitionResult.Failure(
                "高质量识别模型运行失败，请返回设置重新下载模型。");
        }
        finally
        {
            if (engineLockAcquired)
            {
                EngineLock.Release();
            }

            EndEngineUse();
        }
    }

    private static void BeginEngineUse()
    {
        lock (EngineLifecycleLock)
        {
            _activeOperations++;
            EngineUnloadTimer.Change(
                Timeout.InfiniteTimeSpan,
                Timeout.InfiniteTimeSpan);
        }
    }

    private static void EndEngineUse()
    {
        lock (EngineLifecycleLock)
        {
            _activeOperations--;
            if (_activeOperations == 0)
            {
                EngineUnloadTimer.Change(
                    EngineIdleTimeout,
                    Timeout.InfiniteTimeSpan);
            }
        }
    }

    private static void TryUnloadIdleEngine()
    {
        lock (EngineLifecycleLock)
        {
            if (_activeOperations != 0 || !EngineLock.Wait(0))
            {
                EngineUnloadTimer.Change(
                    TimeSpan.FromSeconds(5),
                    Timeout.InfiniteTimeSpan);
                return;
            }

            try
            {
                _engine?.Dispose();
                _engine = null;
                _loadedDirectory = null;
            }
            finally
            {
                EngineLock.Release();
            }
        }
    }

    private static void EnsureEngine(HighQualityOcrModelManager manager)
    {
        if (_engine is not null && string.Equals(
                _loadedDirectory,
                manager.InstallationDirectory,
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _engine?.Dispose();
        var engine = new RapidOcr();
        var modelSet = RapidOcrModelSet.PPOCRv6Small with
        {
            DetModelPath = manager.DetectionModelPath,
            ClsModelPath = manager.ClassificationModelPath,
            RecModelPath = manager.RecognitionModelPath,
            KeysPath = manager.DictionaryPath,
        };
        engine.InitModels(modelSet, HeavyWorkloadBudget.CpuThreadCount);
        _engine = engine;
        _loadedDirectory = manager.InstallationDirectory;
    }

    private static OcrRecognitionResult RecognizeCore(
        CapturedImage capturedImage,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var encoded = new MemoryStream();
        capturedImage.Bitmap.Save(encoded, ImageFormat.Png);
        encoded.Position = 0;
        using var bitmap = SKBitmap.Decode(encoded);
        if (bitmap is null)
        {
            return OcrRecognitionResult.Failure("无法读取待识别图片。");
        }

        var options = RapidOcrOptions.PPOCRv6 with
        {
            ReturnWordBox = true,
            TextScore = 0.45f,
        };
        var result = _engine!.Detect(bitmap, options);
        var blocks = result.TextBlocks
            .Where(block => !string.IsNullOrWhiteSpace(block.Text))
            .OrderBy(block => block.BoxPoints.Min(point => point.Y))
            .ThenBy(block => block.BoxPoints.Min(point => point.X))
            .ToArray();
        var regions = blocks
            .Select(block => CreateRegion(block.Text, block.BoxPoints))
            .ToArray();
        var words = blocks
            .SelectMany(block => block.WordResults is { Length: > 0 }
                ? block.WordResults.Select(word =>
                    CreateWord(word.Text, word.BoxPoints))
                : [CreateWord(block.Text, block.BoxPoints)])
            .Where(word => !string.IsNullOrWhiteSpace(word.Text))
            .ToArray();
        return new OcrRecognitionResult(
            true,
            string.Join(Environment.NewLine, blocks.Select(block => block.Text)),
            ErrorMessage: null)
        {
            Regions = regions,
            Words = words,
        };
    }

    private static OcrTextRegion CreateRegion(
        string text,
        IReadOnlyList<SKPointI> points)
    {
        var left = points.Min(point => point.X);
        var top = points.Min(point => point.Y);
        var right = points.Max(point => point.X);
        var bottom = points.Max(point => point.Y);
        var height = Math.Max(1, bottom - top);
        return new OcrTextRegion(
            text.Trim(),
            left,
            top,
            Math.Max(1, right - left),
            height)
        {
            EstimatedFontSize = Math.Clamp(height / 1.12, 8, 64),
        };
    }

    private static OcrWordRegion CreateWord(
        string text,
        IReadOnlyList<SKPointI> points)
    {
        var region = CreateRegion(text, points);
        return new OcrWordRegion(
            region.Text,
            region.X,
            region.Y,
            region.Width,
            region.Height);
    }
}
