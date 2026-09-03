using System.Drawing.Imaging;
using RapidOcrNet;
using Screenshot.App.Capture;
using Screenshot.App.Core;
using SkiaSharp;
using System.IO;

namespace Screenshot.App.Text;

public static class HighQualityOcrService
{
    // Model initialization is the dominant cost of the first local OCR run.
    // Keep the native engine warm for a few minutes so repeated captures do
    // not pay that cost again on slower machines.
    private static readonly TimeSpan EngineIdleTimeout = TimeSpan.FromMinutes(5);
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
            return await Task.Run(
                () =>
                {
                    // OCR itself is native CPU work. Keep a small fixed budget
                    // instead of borrowing every logical processor: on
                    // low-power/mobile CPUs ONNX's thread pool otherwise
                    // oversubscribes the cores and makes a 600px crop slower.
                    EnsureEngine(modelManager, HeavyWorkloadBudget.OcrThreadCount);
                    return RecognizeCore(capturedImage, cancellationToken);
                },
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
                return await Task.Run(
                    () =>
                    {
                        EnsureEngine(modelManager, HeavyWorkloadBudget.OcrThreadCount);
                        return RecognizeCore(capturedImage, cancellationToken);
                    },
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

    /// <summary>
    /// Loads the local OCR model during an idle period. The work is serialized
    /// with real recognition and never runs on the WPF dispatcher.
    /// </summary>
    public static Task PrewarmAsync(CancellationToken cancellationToken = default)
    {
        var manager = HighQualityOcrModelManager.Shared;
        if (!manager.GetStatus().IsInstalled)
        {
            return Task.CompletedTask;
        }

        return Task.Run(async () =>
        {
            var previousPriority = Thread.CurrentThread.Priority;
            try
            {
                Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
            }
            catch
            {
            }

            BeginEngineUse();
            var acquired = false;
            try
            {
                CaptureTimingDiagnostics.Mark("ocr-engine-prewarm-start");
                await EngineLock.WaitAsync(cancellationToken);
                acquired = true;
                // Prewarming overlaps the user's capture gesture. Keep the
                // native session deliberately background-sized so it cannot
                // starve the compositor or pointer input on a low-end CPU.
                EnsureEngine(manager, HeavyWorkloadBudget.CpuThreadCount);
                CaptureTimingDiagnostics.Mark("ocr-engine-prewarm-end");
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                // Prewarming is best-effort. A missing/incompatible native
                // runtime must not surface as an unobserved task exception;
                // the normal recognition path will report its own failure.
                CaptureTimingDiagnostics.Mark(
                    "ocr-engine-prewarm-failed",
                    $"error={exception.GetType().Name}");
            }
            finally
            {
                if (acquired)
                {
                    EngineLock.Release();
                }

                EndEngineUse();
                try
                {
                    Thread.CurrentThread.Priority = previousPriority;
                }
                catch
                {
                }
            }
        }, cancellationToken);
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

    private static void EnsureEngine(
        HighQualityOcrModelManager manager,
        int cpuThreadCount)
    {
        if (_engine is not null && string.Equals(
                _loadedDirectory,
                manager.InstallationDirectory,
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        using var timing = CaptureTimingDiagnostics.Begin(
            "ocr-engine-init",
            $"directory={manager.InstallationDirectory}");
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
            // Keep the same bounded budget used by the recognition worker.
            // The call runs off the WPF dispatcher, so native model startup
            // cannot synchronously block pointer input or rendering.
            engine.InitModels(modelSet, Math.Max(1, cpuThreadCount));
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
        using (CaptureTimingDiagnostics.Begin(
                   "ocr-image-encode",
                   $"size={capturedImage.Bitmap.Width}x{capturedImage.Bitmap.Height}"))
        {
            capturedImage.Bitmap.Save(encoded, ImageFormat.Png);
        }
        encoded.Position = 0;
        SKBitmap? decodedBitmap;
        using (CaptureTimingDiagnostics.Begin("ocr-image-decode"))
        {
            decodedBitmap = SKBitmap.Decode(encoded);
        }
        using var bitmap = decodedBitmap;
        if (bitmap is null)
        {
            return OcrRecognitionResult.Failure("无法读取待识别图片。");
        }

        // Screenshots are normally upright and the selection is often a
        // narrow UI strip. The stock v6 preset upsizes the short side to 736
        // pixels. A 600x100 capture would therefore become roughly 4400x736
        // before DBNet runs, which is the dominant source of the multi-second
        // delay reported in diagnostics. Keep the native pixels and cap only
        // the long side; this preserves the same detector/recognizer and box
        // output without manufacturing a huge tensor for small UI captures.
        var options = RapidOcrOptions.PPOCRv6 with
        {
            // 960 px is enough resolution for the small UI text normally
            // selected in a screenshot, while avoiding the v6 preset's
            // short-side upscale to several thousand pixels.
            ImgResize = 960,
            LimitSideLen = 0,
            MaxSideLen = 1600,
            DoAngle = false,
            ReturnWordBox = true,
            TextScore = 0.45f,
        };
        using var timing = CaptureTimingDiagnostics.Begin(
            "ocr-detect",
            $"size={bitmap.Width}x{bitmap.Height}");
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
