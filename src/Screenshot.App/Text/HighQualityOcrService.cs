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
        catch (Exception firstException)
        {
            ResetEngine();
            try
            {
                EnsureEngine(modelManager);
                return await Task.Run(
                    () => RecognizeCore(capturedImage, cancellationToken),
                    cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return OcrRecognitionResult.Failure("文字识别已取消。");
            }
            catch (Exception retryException)
            {
                ResetEngine();
                WriteFailureDiagnostics(firstException, retryException);
                return OcrRecognitionResult.Failure(
                    $"高质量识别运行失败（{retryException.GetType().Name}），" +
                    "已自动重试并记录到 ScreenshotData\\Diagnostics\\ocr.log。" +
                    "可暂时切换 Windows OCR。");
            }
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
                ResetEngine();
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

        ResetEngine();
        var engine = new RapidOcr();
        try
        {
            var modelSet = RapidOcrModelSet.PPOCRv6Small with
            {
                DetModelPath = manager.DetectionModelPath,
                ClsModelPath = manager.ClassificationModelPath,
                RecModelPath = manager.RecognitionModelPath,
                KeysPath = manager.DictionaryPath,
            };
            engine.InitModels(modelSet, HeavyWorkloadBudget.CpuThreadCount);
        }
        catch
        {
            engine.Dispose();
            throw;
        }
        _engine = engine;
        _loadedDirectory = manager.InstallationDirectory;
    }

    private static void ResetEngine()
    {
        try
        {
            _engine?.Dispose();
        }
        catch
        {
            // Never keep a failed native OCR engine cached.
        }
        finally
        {
            _engine = null;
            _loadedDirectory = null;
        }
    }

    private static void WriteFailureDiagnostics(
        Exception firstException,
        Exception retryException)
    {
        try
        {
            Directory.CreateDirectory(AppMetadata.DiagnosticsDirectoryPath);
            var path = Path.Combine(
                AppMetadata.DiagnosticsDirectoryPath,
                "ocr.log");
            if (File.Exists(path) && new FileInfo(path).Length > 512 * 1024)
            {
                File.Move(path, path + ".previous", overwrite: true);
            }
            File.AppendAllText(
                path,
                $"[{DateTimeOffset.Now:O}] PP-OCRv6 failed twice.{Environment.NewLine}" +
                $"First attempt:{Environment.NewLine}{firstException}{Environment.NewLine}" +
                $"Retry:{Environment.NewLine}{retryException}{Environment.NewLine}" +
                new string('-', 72) + Environment.NewLine);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
        }
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
