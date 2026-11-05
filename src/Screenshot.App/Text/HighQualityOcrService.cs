using System.Drawing.Imaging;
using RapidOcrNet;
using Screenshot.App.Capture;
using Screenshot.App.Core;
using SkiaSharp;
using System.IO;

namespace Screenshot.App.Text;

public static class HighQualityOcrService
{
    // Reuse native models across a short capture burst, avoiding repeat loads,
    // then release the native sessions
    // instead of retaining their arena buffers for minutes in the tray.
    private static readonly TimeSpan EngineIdleTimeout = TimeSpan.FromSeconds(30);
    private static readonly object EngineLifecycleLock = new();
    private static readonly SemaphoreSlim EngineLock = new(1, 1);
    private static readonly System.Threading.Timer EngineUnloadTimer = BackgroundTimer.Create(
        static _ => TryUnloadIdleEngine());
    private static RapidOcr? _engine;
    private static RapidOcr? _artisticEngine;
    private static string? _loadedDirectory;
    private static int _activeOperations;
    private static long _lastEngineUseTick;

    public static Task<OcrRecognitionResult> RecognizeAsync(
        CapturedImage capturedImage,
        HighQualityOcrModelManager? modelManager = null,
        CancellationToken cancellationToken = default) =>
        OcrWorkerProcess.RecognizeAsync(capturedImage, modelManager, cancellationToken);

    internal static async Task<OcrRecognitionResult> RecognizeInProcessAsync(
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
                    // Native thread pools scale with this machine's capacity;
                    // initialization and recognition use the same budget.
                    var preparation = System.Diagnostics.Stopwatch.StartNew();
                    var threads = HeavyWorkloadBudget.OcrThreadCount;
                    EnsureEngine(modelManager, threads);
                    var preparationMs = preparation.Elapsed.TotalMilliseconds;
                    preparation.Restart();
                    var result = RecognizeCore(capturedImage, modelManager, cancellationToken);
                    return result with { Performance = new(preparationMs, preparation.Elapsed.TotalMilliseconds,
                        threads, _engine!.RecognitionParallelism) { UsedArtisticModel = result.Performance?.UsedArtisticModel ?? false,
                        ArtisticMilliseconds = result.Performance?.ArtisticMilliseconds ?? 0 } };
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
                        return RecognizeCore(capturedImage, modelManager, cancellationToken);
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
                await Task.Run(() => WriteFailureDiagnostics(firstException, retryException), CancellationToken.None).ConfigureAwait(false);
                return OcrRecognitionResult.Failure(
                    $"高质量识别运行失败：{OcrFailureDetails.Describe(retryException)} " +
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
    /// Prepares the isolated worker only after the user starts OCR selection.
    /// The work is serialized with recognition and never runs on the dispatcher.
    /// </summary>
    public static Task PrewarmAsync(CancellationToken cancellationToken = default) =>
        OcrWorkerProcess.PrepareAsync(HighQualityOcrModelManager.Shared, cancellationToken);

    internal static Task PrepareInProcessAsync(HighQualityOcrModelManager manager, CancellationToken cancellationToken)
    {
        // The native runtime belongs to the short-lived worker. Loading it in
        // the tray process would defeat idle release even after Session.Dispose.
        if (!OcrWorkerProcess.IsWorker) return Task.CompletedTask;
        if (!manager.GetStatus().IsInstalled)
        {
            return Task.CompletedTask;
        }

        return Task.Run(async () =>
        {
            var previousPriority = Thread.CurrentThread.Priority;
            try
            {
                Thread.CurrentThread.Priority = ThreadPriority.Normal;
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
                // Do not cache a smaller pool than foreground recognition:
                // ONNX fixes its worker count when the session is initialized.
                EnsureEngine(manager, HeavyWorkloadBudget.OcrThreadCount);
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
                _lastEngineUseTick = Environment.TickCount64;
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
            // A previously queued timer callback can arrive after a new OCR
            // request. Recheck the latest use rather than retiring a hot engine.
            if (_activeOperations != 0) return;
            var remaining = EngineIdleTimeout -
                TimeSpan.FromMilliseconds(Environment.TickCount64 - _lastEngineUseTick);
            if (remaining > TimeSpan.Zero)
            {
                EngineUnloadTimer.Change(remaining, Timeout.InfiniteTimeSpan);
                return;
            }
            if (!EngineLock.Wait(0))
            {
                EngineUnloadTimer.Change(
                    TimeSpan.FromSeconds(5),
                    Timeout.InfiniteTimeSpan);
                return;
            }
        }
        // Native session disposal can take time. Do not hold the lifecycle
        // lock needed by a new request on the dispatcher; it can await EngineLock.
        try
        {
            ResetEngine();
        }
        finally
        {
            EngineLock.Release();
        }
        MemoryFootprint.TrimAfterHeavyOperation();
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
        var modelSet = RapidOcrModelSet.PPOCRv6Small with
        {
            DetModelPath = manager.DetectionModelPath,
            ClsModelPath = manager.ClassificationModelPath,
            RecModelPath = manager.RecognitionModelPath,
            KeysPath = manager.DictionaryPath,
        };
        // Initialization and recognition use the same hardware-aware budget.
        _engine = CreateEngine(modelSet, cpuThreadCount);
        _loadedDirectory = manager.InstallationDirectory;
    }

    private static RapidOcr ArtisticEngine(HighQualityOcrModelManager manager)
    {
        return _artisticEngine ??= CreateEngine(RapidOcrModelSet.PPOCRv6Medium with
        {
            DetModelPath = manager.ArtisticDetectionModelPath,
            RecModelPath = manager.ArtisticRecognitionModelPath,
            ClsModelPath = manager.ClassificationModelPath,
            KeysPath = manager.DictionaryPath,
        }, HeavyWorkloadBudget.OcrThreadCount);
    }

    internal static RapidOcr CreateEngine(RapidOcrModelSet modelSet, int cpuThreadCount)
    {
        var engine = new RapidOcr();
        try
        {
            using var options = RapidOcr.GetDefaultSessionOptions(Math.Max(1, cpuThreadCount));
            // Enable CPU layout optimizations as well as graph fusion. Same-model
            // comparisons retain the input pixels and recognized text.
            options.GraphOptimizationLevel = Microsoft.ML.OnnxRuntime.GraphOptimizationLevel.ORT_ENABLE_ALL;
            options.InterOpNumThreads = 1;
            engine.RecognitionParallelism = ComputeResourceBudget.OcrRecognitionParallelism(
                cpuThreadCount, LocalTranslationStartupPolicy.AvailableRamBytes());
            // The detector and recognizer own separate native pools. Sleeping
            // idle pools avoids stealing cores from the next stage/translation;
            // same-page validation found no loss of throughput or text quality.
            options.AddSessionConfigEntry("session.intra_op.allow_spinning", "0");
            options.AddSessionConfigEntry("session.inter_op.allow_spinning", "0");
            engine.InitModels(modelSet, options);
            return engine;
        }
        catch (Exception initializationException)
        {
            // RapidOcrNet 3.0.0 Dispose dereferences sessions that may not have
            // initialized. Preserve the original failure and its stack; record
            // cleanup separately. The failed worker is retired by its parent.
            try { engine.Dispose(); }
            catch (Exception cleanupException)
            {
                initializationException.Data[OcrFailureDetails.CleanupErrorKey] = cleanupException.ToString();
            }
            throw;
        }
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
            try { _artisticEngine?.Dispose(); } catch { }
            _artisticEngine = null;
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
                OcrFailureDetails.EnvironmentSummary() + Environment.NewLine +
                $"First attempt:{Environment.NewLine}{OcrFailureDetails.FormatException(firstException)}{Environment.NewLine}" +
                $"Retry:{Environment.NewLine}{OcrFailureDetails.FormatException(retryException)}{Environment.NewLine}" +
                new string('-', 72) + Environment.NewLine);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static OcrRecognitionResult RecognizeCore(
        CapturedImage image, HighQualityOcrModelManager manager, CancellationToken token)
    {
        var fast = RecognizeFastCore(image, token, out var needsArtistic, out var layoutBlocks);
        if (!needsArtistic || !manager.HasArtisticModel) return fast;
        token.ThrowIfCancellationRequested();
        // Readable horizontal pages never initialize Medium. Difficult small
        // images and structurally detected mixed-layout posters opt in.
        using var original = CopyBitmapPixels(image.Bitmap);
        var enhancement = System.Diagnostics.Stopwatch.StartNew();
        if (PosterOcrRecovery.NeedsRecovery(original, layoutBlocks))
        {
            var poster = PosterOcrRecovery.JoinColumns(ArtisticOcrMerge.Merge(fast,
                PosterOcrRecovery.Recover(ArtisticEngine(manager), original, layoutBlocks, token)));
            return poster with { Performance = new(0, 0, 0, 0) { UsedArtisticModel = true, ArtisticMilliseconds = enhancement.Elapsed.TotalMilliseconds } };
        }
        var enhanced = RecognizeBitmapCore(original, token, ArtisticEngine(manager));
        return ArtisticOcrMerge.Merge(fast, enhanced) with { Performance = new(0, 0, 0, 0)
            { UsedArtisticModel = true, ArtisticMilliseconds = enhancement.Elapsed.TotalMilliseconds } };
    }

    internal static bool NeedsArtisticEnhancement(int width, int height, OcrRecognitionResult firstPass) =>
        Math.Max(width, height) <= 1000 && (long)width * height <= 480_000 &&
        (firstPass.Regions.Count == 0 || firstPass.Regions.Count <= 4 &&
            firstPass.Regions.Any(region => region.Height > region.Width * 2.2));

    private static OcrRecognitionResult RecognizeFastCore(
        CapturedImage capturedImage,
        CancellationToken cancellationToken, out bool needsArtistic, out TextBlock[] layoutBlocks)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var bitmap = CopyBitmapPixels(capturedImage.Bitmap);
        TextBlock[] detected = [];
        var recognition = RecognizeBitmapCore(bitmap, cancellationToken, inspect: blocks => detected = blocks);
        layoutBlocks = detected;
        needsArtistic = NeedsArtisticEnhancement(bitmap.Width, bitmap.Height, recognition) ||
            PosterOcrRecovery.NeedsRecovery(bitmap, detected);
        if (Math.Min(bitmap.Width, bitmap.Height) <= 96 && Math.Max(bitmap.Width, bitmap.Height) <= 800 &&
            recognition.Regions.Count <= 8)
        {
            // Detector stride and clipped context can hide intact glyphs in a
            // tight UI strip. Add background context once, without stretching
            // pixels; accept only a complete expansion of the existing text.
            const int padding = 12;
            using var padded = new SKBitmap(bitmap.Width + padding * 2, bitmap.Height + padding * 2);
            using (var canvas = new SKCanvas(padded))
            {
                canvas.Clear(bitmap.GetPixel(0, 0));
                canvas.DrawBitmap(bitmap, padding, padding);
            }
            var candidate = RecognizeBitmapCore(padded, cancellationToken);
            if (IsCompleteSmallTextExpansion(recognition.Text, candidate.Text))
            {
                var mapped = candidate with
                {
                    Regions = candidate.Regions.Select(r => r with
                    {
                        X = Math.Clamp(r.X - padding, 0, bitmap.Width), Y = Math.Clamp(r.Y - padding, 0, bitmap.Height),
                        Width = Math.Max(0, Math.Min(bitmap.Width, r.X + r.Width - padding) - Math.Max(0, r.X - padding)),
                        Height = Math.Max(0, Math.Min(bitmap.Height, r.Y + r.Height - padding) - Math.Max(0, r.Y - padding)),
                    }).Where(r => r.Width > 0 && r.Height > 0).ToArray(),
                    Words = candidate.Words.Select(w => w with
                    {
                        X = Math.Clamp(w.X - padding, 0, bitmap.Width), Y = Math.Clamp(w.Y - padding, 0, bitmap.Height),
                        Width = Math.Max(0, Math.Min(bitmap.Width, w.X + w.Width - padding) - Math.Max(0, w.X - padding)),
                        Height = Math.Max(0, Math.Min(bitmap.Height, w.Y + w.Height - padding) - Math.Max(0, w.Y - padding)),
                    }).Where(w => w.Width > 0 && w.Height > 0).ToArray(),
                };
                var mappedText = string.Join(Environment.NewLine, mapped.Regions.Select(r => r.Text));
                if (IsCompleteSmallTextExpansion(recognition.Text, mappedText))
                    recognition = mapped with { Text = mappedText };
            }
        }
        var narrowSmallText = Math.Min(bitmap.Width, bitmap.Height) <= 96 &&
            recognition.Regions.Count > 0 && recognition.Regions.All(region => region.Height <= 28);
        if ((!narrowSmallText && recognition.Regions.Count > 0) ||
            Math.Max(bitmap.Width, bitmap.Height) > 800 || Math.Min(bitmap.Width, bitmap.Height) > 400)
        {
            return recognition;
        }

        // DBNet can miss a short UI label when stride rounding changes by only
        // a few pixels. Medium can also find the Chinese part of a tiny label
        // but miss its Latin/numeric suffix. Retry once at 2x with unchanged
        // thresholds. A partial result is replaced only when every original
        // character survives in order and the retry adds text. The tensor
        // stays <=1600x800; normal/full-page
        // recognition keeps its existing single pass. This is OCR preprocessing,
        // never an enlargement of the user's saved image.
        cancellationToken.ThrowIfCancellationRequested();
        using var enlarged = capturedImage.CreateScaled(200);
        using var retryBitmap = CopyBitmapPixels(enlarged.Bitmap);
        var retry = RecognizeBitmapCore(retryBitmap, cancellationToken);
        if (recognition.Regions.Count > 0 && !IsCompleteSmallTextExpansion(recognition.Text, retry.Text))
            return recognition;
        return retry with
        {
            Regions = retry.Regions.Select(region => region with
            {
                X = region.X / 2, Y = region.Y / 2,
                Width = region.Width / 2, Height = region.Height / 2,
                EstimatedFontSize = Math.Clamp(region.Height / 2 / 1.12, 8, 64),
            }).ToArray(),
            Words = retry.Words.Select(word => word with
            {
                X = word.X / 2, Y = word.Y / 2,
                Width = word.Width / 2, Height = word.Height / 2,
            }).ToArray(),
        };
    }

    internal static bool IsCompleteSmallTextExpansion(string original, string candidate)
    {
        var originalText = string.Concat(original.Where(character => !char.IsWhiteSpace(character)));
        var candidateText = string.Concat(candidate.Where(character => !char.IsWhiteSpace(character)));
        if (candidateText.Length <= originalText.Length) return false;
        var matched = 0;
        foreach (var character in candidateText)
        {
            if (matched < originalText.Length && character == originalText[matched]) matched++;
        }
        return matched == originalText.Length;
    }

    private static OcrRecognitionResult RecognizeBitmapCore(SKBitmap bitmap, CancellationToken cancellationToken,
        RapidOcr? engine = null, Action<TextBlock[]>? inspect = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        engine ??= _engine!;

        // Screenshots are normally upright and the selection is often a
        // narrow UI strip. The stock v6 preset upsizes the short side to 736
        // pixels. A 600x100 capture would therefore become roughly 4400x736
        // before DBNet runs, which is the dominant source of the multi-second
        // delay reported in diagnostics. Keep the native pixels and cap only
        // the long side; this preserves the same detector/recognizer and box
        // output without manufacturing a huge tensor for small UI captures.
        var options = RapidOcrOptions.PPOCRv6 with
        {
            // Preserve small text in dense web pages. The old 960-pixel resize
            // discarded strokes in links and numbers; still cap large captures
            // at 1600 and never enlarge narrow selections to a full-page tensor.
            ImgResize = Math.Min(1600, Math.Max(bitmap.Width, bitmap.Height)),
            LimitSideLen = 0,
            MaxSideLen = 1600,
            DoAngle = false,
            ReturnWordBox = true,
            TextScore = 0.45f,
        };
        using var timing = CaptureTimingDiagnostics.Begin(
            "ocr-detect",
            $"size={bitmap.Width}x{bitmap.Height}");
        // Inspect tall low-confidence geometry before applying the unchanged
        // 0.45 output threshold; a rotated upright column otherwise disappears.
        var result = engine.Detect(bitmap, options with { TextScore = 0 });
        inspect?.Invoke(result.TextBlocks);
        cancellationToken.ThrowIfCancellationRequested();
        var blocks = VerticalOcrRecovery.Recover(engine, bitmap, options, result.TextBlocks, cancellationToken)
            .Where(block => !string.IsNullOrWhiteSpace(block.Text))
            .OrderBy(block => block.BoxPoints.Min(point => point.Y))
            .ThenBy(block => block.BoxPoints.Min(point => point.X))
            .ToArray();
        var regions = OcrReadingOrder.Sort(blocks
            .Select(block => CreateRegion(block.Text, block.BoxPoints) with
            {
                RecognitionConfidence = VerticalOcrRecovery.Score(block),
                RecoveredVerticalColumn = !result.TextBlocks.Any(raw => ReferenceEquals(raw, block)),
            })
            .ToArray());
        var words = blocks
            .SelectMany(block => block.WordResults is { Length: > 0 }
                ? block.WordResults.Select(word =>
                    CreateWord(word.Text, word.BoxPoints))
                : [CreateWord(block.Text, block.BoxPoints)])
            .Where(word => !string.IsNullOrWhiteSpace(word.Text))
            .ToArray();
        return new OcrRecognitionResult(
            true,
            string.Join(Environment.NewLine, regions.Select(region => region.Text)),
            ErrorMessage: null)
        {
            Regions = regions,
            Words = words,
        };
    }

    internal static SKBitmap CopyBitmapPixels(System.Drawing.Bitmap source)
    {
        // GDI supplies straight-alpha BGRA rows. Copy directly to Skia without
        // PNG compression, allocation and decompression. LockBits also converts
        // 24-bit/indexed inputs; signed source stride preserves top/bottom order.
        var bitmap = new SKBitmap(source.Width, source.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        try
        {
            var bounds = new System.Drawing.Rectangle(0, 0, source.Width, source.Height);
            var pixels = source.LockBits(bounds, ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            try
            {
                var row = new byte[checked(source.Width * 4)];
                for (var y = 0; y < source.Height; y++)
                {
                    System.Runtime.InteropServices.Marshal.Copy(IntPtr.Add(pixels.Scan0, y * pixels.Stride), row, 0, row.Length);
                    System.Runtime.InteropServices.Marshal.Copy(row, 0, IntPtr.Add(bitmap.GetPixels(), y * bitmap.RowBytes), row.Length);
                }
            }
            finally { source.UnlockBits(pixels); }
            return bitmap;
        }
        catch { bitmap.Dispose(); throw; }
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
