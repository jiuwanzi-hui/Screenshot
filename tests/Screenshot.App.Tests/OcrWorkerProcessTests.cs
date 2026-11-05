using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using Screenshot.App.Capture;
using Screenshot.App.Text;

namespace Screenshot.App.Tests;

public sealed class OcrWorkerProcessTests
{
    [Theory]
    [InlineData(PixelFormat.Format24bppRgb)]
    [InlineData(PixelFormat.Format32bppArgb)]
    public async Task PipeTransferPreservesPixelPositionsAndModelPath(PixelFormat format)
    {
        using var source = new Bitmap(23, 13, format);
        source.SetPixel(0, 0, Color.Red);
        source.SetPixel(22, 12, Color.Blue);
        source.SetPixel(7, 3, Color.FromArgb(255, 31, 97, 163));
        using var stream = new MemoryStream();
        await OcrWorkerProcess.WriteImageAsync(stream, source, @"D:\模型\OCR", CancellationToken.None);
        stream.Position = 0;
        var (copy, path) = await OcrWorkerProcess.ReadImageAsync(stream, CancellationToken.None);
        using (copy)
        {
            Assert.Equal(@"D:\模型\OCR", path);
            Assert.Equal(source.Size, copy.Size);
            for (var y = 0; y < source.Height; y++)
                for (var x = 0; x < source.Width; x++)
                    Assert.Equal(source.GetPixel(x, y).ToArgb(), copy.GetPixel(x, y).ToArgb());
        }
    }

    [Fact]
    public async Task TruncatedTransferFailsInsteadOfProducingPartialImage()
    {
        using var source = new Bitmap(20, 20);
        using var stream = new MemoryStream();
        await OcrWorkerProcess.WriteImageAsync(stream, source, "models", CancellationToken.None);
        stream.SetLength(stream.Length - 1);
        stream.Position = 0;
        await Assert.ThrowsAsync<EndOfStreamException>(() => OcrWorkerProcess.ReadImageAsync(stream, CancellationToken.None));
    }

    [OcrWorkerFact]
    public async Task RealWorkerPreservesRecognitionReusesThenExitsAndCanRestart()
    {
        using var manager = new HighQualityOcrModelManager(Environment.GetEnvironmentVariable("SNAPCUT_OCR_MODEL_DIRECTORY"));
        using var image = new CapturedImage(new Bitmap(620, 110));
        using (var graphics = Graphics.FromImage(image.Bitmap))
        using (var font = new Font("Microsoft YaHei UI", 20))
        {
            graphics.Clear(Color.White);
            graphics.DrawString("Screenshot: 25 users\n请保存文档，再选择目标语言。", font, Brushes.Black, 8, 6);
        }
        var nativeBefore = HasOnnxModule();
        var watch = Stopwatch.StartNew();
        var first = await HighQualityOcrService.RecognizeAsync(image, manager);
        Assert.True(first.IsSuccess, first.ErrorMessage);
        Assert.Contains("25", first.Text);
        Assert.NotEmpty(first.Words);
        var firstTime = watch.Elapsed.TotalMilliseconds;
        var worker = Worker();
        var firstPid = worker.Id;
        using var observer = Process.GetProcessById(firstPid);
        watch.Restart();
        var second = await HighQualityOcrService.RecognizeAsync(image, manager);
        var secondTime = watch.Elapsed.TotalMilliseconds;
        Assert.Equal(first.Text, second.Text);
        Assert.Equal(first.Words, second.Words);
        Assert.Equal(firstPid, Worker().Id);
        Assert.Equal(nativeBefore, HasOnnxModule());
        observer.Refresh();
        Console.WriteLine($"OCR worker memory: total working set={observer.WorkingSet64 / 1048576d:F1} MiB, " +
            $"peak total working set={observer.PeakWorkingSet64 / 1048576d:F1} MiB, " +
            $"private commit={observer.PrivateMemorySize64 / 1048576d:F1} MiB.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await observer.WaitForExitAsync(timeout.Token);
        watch.Restart();
        var third = await HighQualityOcrService.RecognizeAsync(image, manager);
        Assert.Equal(first.Text, third.Text);
        Assert.NotEqual(firstPid, Worker().Id);
        Console.WriteLine($"OCR worker: cold={firstTime:F0} ms, warm={secondTime:F0} ms, reload={watch.Elapsed.TotalMilliseconds:F0} ms; first worker exited; parent ONNX loaded={HasOnnxModule()}.");
        using var finalWorker = Process.GetProcessById(Worker().Id);
        using var finalTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await finalWorker.WaitForExitAsync(finalTimeout.Token);
    }

    [OcrWorkerFact]
    public async Task SelectionPreparationReusesNativeSessionAndUnusedCancellationExits()
    {
        using var manager = new HighQualityOcrModelManager(Environment.GetEnvironmentVariable("SNAPCUT_OCR_MODEL_DIRECTORY"));
        var nativeBefore = HasOnnxModule();
        if (typeof(OcrWorkerProcess).GetField("_process", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null) is Process previous)
        {
            using var previousObserver = Process.GetProcessById(previous.Id);
            using var previousTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await previousObserver.WaitForExitAsync(previousTimeout.Token);
        }
        var timer = Stopwatch.StartNew();
        await OcrWorkerProcess.PrepareAsync(manager, CancellationToken.None);
        var preparationMs = timer.ElapsedMilliseconds;
        using var prepared = Process.GetProcessById(Worker().Id);
        Assert.Equal(nativeBefore, HasOnnxModule());
        using var image = new CapturedImage(new Bitmap(620, 110));
        using (var graphics = Graphics.FromImage(image.Bitmap))
        using (var font = new Font("Microsoft YaHei UI", 20))
        {
            graphics.Clear(Color.White);
            graphics.DrawString("Screenshot: 25 users\n请保存文档，再选择目标语言。", font, Brushes.Black, 8, 6);
        }
        timer.Restart();
        var result = await HighQualityOcrService.RecognizeAsync(image, manager);
        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Contains("25", result.Text);
        Assert.Equal(prepared.Id, Worker().Id);
        Console.WriteLine($"Prepared OCR: selection preparation={preparationMs} ms, first image={timer.ElapsedMilliseconds} ms; reused PID={prepared.Id}.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await prepared.WaitForExitAsync(timeout.Token);
        await OcrWorkerProcess.PrepareAsync(manager, CancellationToken.None);
        using var unused = Process.GetProcessById(Worker().Id);
        OcrWorkerProcess.CancelUnusedPreparation();
        using var cancelTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await unused.WaitForExitAsync(cancelTimeout.Token);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await OcrWorkerProcess.PrepareAsync(manager, cancelled.Token);
        Assert.Null(typeof(OcrWorkerProcess).GetField("_process", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null));
    }

    private static Process Worker() => (Process)typeof(OcrWorkerProcess)
        .GetField("_process", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

    [OcrWorkerFact]
    public async Task InvalidModelReportsOriginalFailureRetiresWorkerAndValidModelRecovers()
    {
        using var valid = new HighQualityOcrModelManager(Environment.GetEnvironmentVariable("SNAPCUT_OCR_MODEL_DIRECTORY"));
        var invalidDirectory = Directory.CreateTempSubdirectory("SnapCut-BadOcr-");
        try
        {
            // Same lengths pass installation presence checks but ONNX must
            // reject the actual invalid bytes. Never alter the installed models.
            foreach (var path in new[] { valid.DetectionModelPath, valid.ClassificationModelPath,
                valid.RecognitionModelPath, valid.DictionaryPath })
            {
                using var file = File.Create(Path.Combine(invalidDirectory.FullName, Path.GetFileName(path)));
                file.SetLength(new FileInfo(path).Length);
            }
            using var invalid = new HighQualityOcrModelManager(invalidDirectory.FullName);
            using var image = new CapturedImage(new Bitmap(400, 90));
            using (var graphics = Graphics.FromImage(image.Bitmap))
            using (var font = new Font("Microsoft YaHei UI", 20))
            {
                graphics.Clear(Color.White);
                graphics.DrawString("Recovery 25", font, Brushes.Black, 8, 8);
            }
            var pending = HighQualityOcrService.RecognizeAsync(image, invalid);
            // Process creation is asynchronous so it cannot stall UI hooks.
            // Observe the child once launched, rather than assuming the call
            // has synchronously populated _process before its first await.
            var startup = Stopwatch.StartNew();
            while (typeof(OcrWorkerProcess).GetField("_process", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null) is null &&
                !pending.IsCompleted && startup.Elapsed < TimeSpan.FromSeconds(5))
                await Task.Delay(1);
            using var failedWorker = Process.GetProcessById(Worker().Id);
            var failed = await pending;
            Assert.False(failed.IsSuccess);
            Assert.Contains("OnnxRuntimeException", failed.ErrorMessage);
            Assert.DoesNotContain("NullReferenceException", failed.ErrorMessage);
            Assert.Null(typeof(OcrWorkerProcess).GetField("_process", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await failedWorker.WaitForExitAsync(timeout.Token);

            var recovered = await HighQualityOcrService.RecognizeAsync(image, valid);
            Assert.True(recovered.IsSuccess, recovered.ErrorMessage);
            Assert.Contains("25", recovered.Text);
            using var worker = Process.GetProcessById(Worker().Id);
            using var idleTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await worker.WaitForExitAsync(idleTimeout.Token);
        }
        finally { invalidDirectory.Delete(recursive: true); }
    }

    [OcrWorkerFact]
    public async Task OrdinaryPageUsesSmallEvenWhenArtisticFilesCannotBeLoaded()
    {
        using var valid = new HighQualityOcrModelManager(Environment.GetEnvironmentVariable("SNAPCUT_OCR_MODEL_DIRECTORY"));
        var directory = Directory.CreateTempSubdirectory("SnapCut-SmallOnly-");
        try
        {
            foreach (var path in new[] { valid.DetectionModelPath, valid.ClassificationModelPath,
                valid.RecognitionModelPath, valid.DictionaryPath })
                File.Copy(path, Path.Combine(directory.FullName, Path.GetFileName(path)));
            // Complete-sized but invalid Medium files catch any accidental
            // eager initialization. Do not change the user's real installation.
            foreach (var (name, size) in new[] { ("PP-OCRv6_det_medium.onnx", 62_119_454L),
                ("PP-OCRv6_rec_medium.onnx", 76_629_984L) })
            {
                using var file = File.Create(Path.Combine(directory.FullName, name));
                file.SetLength(size);
            }
            using var manager = new HighQualityOcrModelManager(directory.FullName);
            Assert.True(manager.GetStatus().HasArtisticModel);
            using var image = new CapturedImage(new Bitmap(1000, 500));
            using (var graphics = Graphics.FromImage(image.Bitmap))
            using (var font = new Font("Microsoft YaHei UI", 24))
            {
                graphics.Clear(Color.White);
                graphics.DrawString("Ordinary screenshot 25 users", font, Brushes.Black, 12, 16);
            }
            var result = await HighQualityOcrService.RecognizeAsync(image, manager);
            Assert.True(result.IsSuccess, result.ErrorMessage);
            Assert.Contains("25 users", result.Text);
            using var worker = Process.GetProcessById(Worker().Id);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await worker.WaitForExitAsync(timeout.Token);
        }
        finally { directory.Delete(recursive: true); }
    }

    [OcrWorkerFact]
    public async Task CancelledInferenceRetiresWorkerAndNextRequestRecovers()
    {
        using var manager = new HighQualityOcrModelManager(Environment.GetEnvironmentVariable("SNAPCUT_OCR_MODEL_DIRECTORY"));
        using var image = new CapturedImage(new Bitmap(1600, 900));
        using (var graphics = Graphics.FromImage(image.Bitmap))
        using (var font = new Font("Microsoft YaHei UI", 24))
        {
            graphics.Clear(Color.White);
            for (var y = 0; y < 800; y += 55)
                graphics.DrawString("取消后再识别 Screenshot 25", font, Brushes.Black, 12, y);
        }
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(80));
        var cancelled = await HighQualityOcrService.RecognizeAsync(image, manager, cancellation.Token);
        Assert.False(cancelled.IsSuccess);
        Assert.Contains("取消", cancelled.ErrorMessage);
        Assert.Null(typeof(OcrWorkerProcess).GetField("_process", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null));
        var recovered = await HighQualityOcrService.RecognizeAsync(image, manager);
        Assert.True(recovered.IsSuccess, recovered.ErrorMessage);
        Assert.Contains("25", recovered.Text);
        using var worker = Process.GetProcessById(Worker().Id);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await worker.WaitForExitAsync(timeout.Token);
    }

    private static bool HasOnnxModule()
    {
        using var current = Process.GetCurrentProcess();
        return current.Modules.Cast<ProcessModule>().Any(module =>
            string.Equals(module.ModuleName, "onnxruntime.dll", StringComparison.OrdinalIgnoreCase));
    }

    private sealed class OcrWorkerFactAttribute : FactAttribute
    {
        public OcrWorkerFactAttribute()
        {
            if (!Directory.Exists(Environment.GetEnvironmentVariable("SNAPCUT_OCR_MODEL_DIRECTORY")))
                Skip = "Set SNAPCUT_OCR_MODEL_DIRECTORY to run the isolated native OCR lifecycle test.";
        }
    }
}
