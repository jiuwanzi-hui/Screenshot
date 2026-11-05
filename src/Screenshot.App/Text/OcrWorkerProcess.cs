using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Screenshot.App.Capture;

namespace Screenshot.App.Text;

// Only raw BGRA pixels and local recognition results cross this private pipe.
// Native OCR arenas, thread pools and DLL state die with the worker, rather
// than remaining in the tray process after the inference sessions are disposed.
internal static class OcrWorkerProcess
{
    internal const string WorkerArgument = "--ocr-worker";
    internal static bool IsWorker { get; private set; }
    internal static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(10);
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly System.Threading.Timer IdleTimer = Core.BackgroundTimer.Create(
        static _ => RetireIdleWorker());
    private static Process? _process;
    private static NamedPipeServerStream? _pipe;
    private static long _lastUse;
    private static bool _preparedOnly;
    private static long _activityVersion;

    // Invoked only after the user starts OCR selection. No pixels or native
    // model are loaded in the UI/resident process, and no idle preload occurs.
    internal static async Task PrepareAsync(HighQualityOcrModelManager manager, CancellationToken token)
    {
        Interlocked.Increment(ref _activityVersion);
        if (!manager.GetStatus().IsInstalled) return;
        var acquired = false;
        try
        {
            await Gate.WaitAsync(token).ConfigureAwait(false); acquired = true;
            if (_process is { HasExited: false } && _pipe?.IsConnected == true) return;
            IdleTimer.Change(Timeout.Infinite, Timeout.Infinite);
            await EnsureWorkerAsync(token).ConfigureAwait(false);
            _preparedOnly = true;
            await WriteIntAsync(_pipe!, -1, token).ConfigureAwait(false);
            var path = Encoding.UTF8.GetBytes(manager.InstallationDirectory);
            if (path.Length > 32_768) throw new IOException("模型路径过长。");
            await WriteIntAsync(_pipe!, path.Length, token).ConfigureAwait(false);
            await _pipe!.WriteAsync(path, token).ConfigureAwait(false);
            if (await ReadIntAsync(_pipe, token).ConfigureAwait(false) != 1)
                throw new IOException("识别准备未完成。");
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException or
            InvalidOperationException or System.ComponentModel.Win32Exception)
        { if (acquired) StopWorker(); }
        finally
        {
            if (acquired)
            {
                _lastUse = Environment.TickCount64;
                if (_process is not null) IdleTimer.Change(IdleTimeout, Timeout.InfiniteTimeSpan);
                Gate.Release();
            }
        }
    }

    internal static void CancelUnusedPreparation()
    {
        var version = Volatile.Read(ref _activityVersion);
        _ = Task.Run(async () =>
        {
            await Gate.WaitAsync().ConfigureAwait(false);
            try { if (_preparedOnly && version == Volatile.Read(ref _activityVersion)) StopWorker(); }
            finally { Gate.Release(); }
        });
    }

    internal static async Task<OcrRecognitionResult> RecognizeAsync(CapturedImage image,
        HighQualityOcrModelManager? manager, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(image);
        var total = Stopwatch.StartNew();
        Interlocked.Increment(ref _activityVersion);
        manager ??= HighQualityOcrModelManager.Shared;
        if (!manager.GetStatus().IsInstalled)
            return OcrRecognitionResult.Failure("高质量识别模型尚未下载，请到“内容识别”设置中安装。");
        var acquired = false;
        try
        {
            await Gate.WaitAsync(token).ConfigureAwait(false);
            acquired = true;
            var queueMs = total.Elapsed.TotalMilliseconds;
            _preparedOnly = false;
            IdleTimer.Change(Timeout.Infinite, Timeout.Infinite);
            var workerPreparation = Stopwatch.StartNew();
            await EnsureWorkerAsync(token).ConfigureAwait(false);
            workerPreparation.Stop();
            // Pixel copies and native LockBits are never performed on the UI thread.
            var result = await Task.Run(async () =>
            {
                await WriteImageAsync(_pipe!, image.Bitmap, manager.InstallationDirectory, token).ConfigureAwait(false);
                return await ReadResultAsync(_pipe!, token).ConfigureAwait(false);
            }, token).ConfigureAwait(false);
            // A failed initialization can leave partially created native
            // sessions that the dependency cannot dispose. Do not reuse or
            // retain that worker after reporting the failure.
            if (!result.IsSuccess) StopWorker();
            return result.Performance is { } performance
                ? result with { Performance = performance with { WorkerStartMilliseconds = workerPreparation.Elapsed.TotalMilliseconds,
                    QueueMilliseconds = queueMs, TotalMilliseconds = total.Elapsed.TotalMilliseconds,
                    ImageWidth = image.Bitmap.Width, ImageHeight = image.Bitmap.Height } }
                : result;
        }
        catch (OperationCanceledException)
        {
            if (acquired) StopWorker(); // Cancels native inference, not just its awaiter.
            return OcrRecognitionResult.Failure("文字识别已取消。");
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or
            System.ComponentModel.Win32Exception or JsonException or TimeoutException)
        {
            if (acquired) StopWorker();
            return OcrRecognitionResult.Failure("高质量识别工作进程未能完成，本次已释放资源，请重试。" +
                $"（{exception.GetType().Name}）");
        }
        finally
        {
            if (acquired)
            {
                _lastUse = Environment.TickCount64;
                if (_process is not null) IdleTimer.Change(IdleTimeout, Timeout.InfiniteTimeSpan);
                Gate.Release();
            }
        }
    }

    private static async Task EnsureWorkerAsync(CancellationToken token)
    {
        if (_process is { HasExited: false } && _pipe?.IsConnected == true) return;
        StopWorker();
        var name = "SnapCut-Ocr-" + Guid.NewGuid().ToString("N");
        _pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var executable = Path.Combine(AppContext.BaseDirectory, "SnapCut.exe");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = AppContext.BaseDirectory };
        start.ArgumentList.Add(WorkerArgument);
        start.ArgumentList.Add(name);
        start.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        _process = await Task.Run(() => Process.Start(start), token).ConfigureAwait(false)
            ?? throw new IOException("无法启动识别工作进程。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        await _pipe.WaitForConnectionAsync(timeout.Token).ConfigureAwait(false);
        if (!GetNamedPipeClientProcessId(_pipe.SafePipeHandle, out var connectedPid) || connectedPid != _process.Id)
            throw new IOException("识别管道连接不匹配。");
    }

    private static void RetireIdleWorker()
    {
        if (!Gate.Wait(0)) return; // Completion schedules the next one-shot check.
        try
        {
            var remaining = IdleTimeout - TimeSpan.FromMilliseconds(Environment.TickCount64 - _lastUse);
            if (remaining > TimeSpan.Zero) IdleTimer.Change(remaining, Timeout.InfiniteTimeSpan);
            else StopWorker();
        }
        finally { Gate.Release(); }
    }

    private static void StopWorker()
    {
        _pipe?.Dispose();
        _pipe = null;
        var process = _process;
        _process = null;
        _preparedOnly = false;
        if (process is null) return;
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
        finally { process.Dispose(); }
    }

    internal static async Task<int> RunWorkerAsync(string[] args)
    {
        if (args.Length != 3 || !args[1].StartsWith("SnapCut-Ocr-", StringComparison.Ordinal) ||
            !int.TryParse(args[2], out var parentId)) return 2;
        IsWorker = true;
        Infrastructure.NativeEfficiency.Apply(false);
        try
        {
            using var parent = Process.GetProcessById(parentId);
            parent.EnableRaisingEvents = true;
            parent.Exited += static (_, _) => Environment.Exit(0);
            if (parent.HasExited) return 0;
            using var pipe = new NamedPipeClientStream(".", args[1], PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(15_000).ConfigureAwait(false);
            while (pipe.IsConnected)
            {
                // Also self-expire if the client fails to send another request.
                using var idle = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                var length = await ReadIntAsync(pipe, idle.Token).ConfigureAwait(false);
                if (length == -1)
                {
                    var pathLength = await ReadIntAsync(pipe, idle.Token).ConfigureAwait(false);
                    if (pathLength is <= 0 or > 32_768) throw new IOException("无效模型路径。");
                    var path = new byte[pathLength];
                    await pipe.ReadExactlyAsync(path, idle.Token).ConfigureAwait(false);
                    using var preparedManager = new HighQualityOcrModelManager(Encoding.UTF8.GetString(path));
                    await HighQualityOcrService.PrepareInProcessAsync(preparedManager, idle.Token).ConfigureAwait(false);
                    await WriteIntAsync(pipe, 1, CancellationToken.None).ConfigureAwait(false);
                    await pipe.FlushAsync().ConfigureAwait(false);
                    continue;
                }
                var (bitmap, directory) = await ReadImageCoreAsync(pipe, length, idle.Token).ConfigureAwait(false);
                using var image = new CapturedImage(bitmap);
                using var manager = new HighQualityOcrModelManager(directory);
                var result = await HighQualityOcrService.RecognizeInProcessAsync(image, manager).ConfigureAwait(false);
                var payload = JsonSerializer.SerializeToUtf8Bytes(result);
                await WriteIntAsync(pipe, payload.Length, CancellationToken.None).ConfigureAwait(false);
                await pipe.WriteAsync(payload).ConfigureAwait(false);
                await pipe.FlushAsync().ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException or
            ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
        return 0;
    }

    internal static async Task WriteImageAsync(Stream stream, Bitmap image, string directory, CancellationToken token)
    {
        var path = Encoding.UTF8.GetBytes(directory);
        if (path.Length > 32_768) throw new IOException("模型路径过长。");
        await WriteIntAsync(stream, path.Length, token).ConfigureAwait(false);
        await stream.WriteAsync(path, token).ConfigureAwait(false);
        await WriteIntAsync(stream, image.Width, token).ConfigureAwait(false);
        await WriteIntAsync(stream, image.Height, token).ConfigureAwait(false);
        var row = new byte[checked(image.Width * 4)];
        var data = image.LockBits(new Rectangle(0, 0, image.Width, image.Height), ImageLockMode.ReadOnly,
            PixelFormat.Format32bppArgb);
        try
        {
            for (var y = 0; y < image.Height; y++)
            {
                token.ThrowIfCancellationRequested();
                Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, row.Length);
                await stream.WriteAsync(row, token).ConfigureAwait(false);
            }
        }
        finally { image.UnlockBits(data); }
        await stream.FlushAsync(token).ConfigureAwait(false);
    }

    internal static async Task<(Bitmap Bitmap, string Directory)> ReadImageAsync(Stream stream, CancellationToken token)
    {
        var pathLength = await ReadIntAsync(stream, token).ConfigureAwait(false);
        return await ReadImageCoreAsync(stream, pathLength, token).ConfigureAwait(false);
    }

    private static async Task<(Bitmap Bitmap, string Directory)> ReadImageCoreAsync(Stream stream, int pathLength, CancellationToken token)
    {
        if (pathLength is <= 0 or > 32_768) throw new IOException("无效模型路径。");
        var path = new byte[pathLength];
        await stream.ReadExactlyAsync(path, token).ConfigureAwait(false);
        var width = await ReadIntAsync(stream, token).ConfigureAwait(false);
        var height = await ReadIntAsync(stream, token).ConfigureAwait(false);
        if (width <= 0 || height <= 0 || (long)width * height > int.MaxValue / 4)
            throw new IOException("识别图像尺寸无效或超出可寻址范围。");
        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        try
        {
            var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                var row = new byte[checked(width * 4)];
                for (var y = 0; y < height; y++)
                {
                    await stream.ReadExactlyAsync(row, token).ConfigureAwait(false);
                    Marshal.Copy(row, 0, data.Scan0 + y * data.Stride, row.Length);
                }
            }
            finally { bitmap.UnlockBits(data); }
            return (bitmap, Encoding.UTF8.GetString(path));
        }
        catch { bitmap.Dispose(); throw; }
    }

    private static async Task<OcrRecognitionResult> ReadResultAsync(Stream stream, CancellationToken token)
    {
        var length = await ReadIntAsync(stream, token).ConfigureAwait(false);
        if (length is <= 0 or > 64 * 1024 * 1024) throw new IOException("识别结果大小无效。");
        var bytes = new byte[length];
        await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        return JsonSerializer.Deserialize<OcrRecognitionResult>(bytes) ?? throw new IOException("识别结果为空。");
    }

    private static async Task<int> ReadIntAsync(Stream stream, CancellationToken token)
    {
        var bytes = new byte[4];
        await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        return System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(bytes);
    }

    private static Task WriteIntAsync(Stream stream, int value, CancellationToken token)
    {
        var bytes = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        return stream.WriteAsync(bytes, token).AsTask();
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out int processId);
}
