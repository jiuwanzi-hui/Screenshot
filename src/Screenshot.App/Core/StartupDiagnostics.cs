using System.IO;
using System.Text;
using System.Threading.Channels;

namespace Screenshot.App.Core;

/// <summary>
/// 启动和快捷键诊断日志记录器
/// </summary>
public static class StartupDiagnostics
{
    private static readonly object LogLock = new();
    private static readonly string LogFilePath = Path.Combine(
        AppMetadata.DiagnosticsDirectoryPath,
        "startup-diagnostics.log");
    private const long MaxLogSizeBytes = 5 * 1024 * 1024; // 5MB
    private static readonly Channel<string> Queue =
        Channel.CreateBounded<string>(new BoundedChannelOptions(4096)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest,
        });
    private static int _writerStarted;
    private static Task? _writerTask;
    private static bool IsEnabled
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(
                    Environment.GetEnvironmentVariable("SNAPCUT_CAPTURE_DIAGNOSTICS")))
            {
                return true;
            }

            try
            {
                return File.Exists(Path.Combine(
                    AppMetadata.DiagnosticsDirectoryPath,
                    "capture-timing.enabled"));
            }
            catch
            {
                return false;
            }
        }
    }

    public static void LogElevation(string message)
    {
        Log($"[ELEVATION] {message}");
    }

    public static void LogHotKey(string message)
    {
        Log($"[HOTKEY] {message}");
    }

    public static void LogWindowCreation(string message)
    {
        Log($"[WINDOW] {message}");
    }

    private static void Log(string message)
    {
        if (!IsEnabled)
        {
            return;
        }

        try
        {
            var timestamp = DateTime.Now.ToString(
                "yyyy-MM-dd HH:mm:ss.fff",
                System.Globalization.CultureInfo.InvariantCulture);
            EnsureWriter();
            Queue.Writer.TryWrite($"[{timestamp}] {message}");
        }
        catch
        {
            // 诊断日志失败不应该影响程序运行
        }
    }

    public static void ClearOldLogs()
    {
        try
        {
            lock (LogLock)
            {
                if (File.Exists(LogFilePath))
                {
                    var lastWrite = File.GetLastWriteTime(LogFilePath);
                    if (DateTime.Now - lastWrite > TimeSpan.FromDays(7))
                    {
                        File.Delete(LogFilePath);
                    }
                }
            }
        }
        catch
        {
            // 清理失败不应该影响程序运行
        }
    }

    private static void EnsureWriter()
    {
        if (Interlocked.Exchange(ref _writerStarted, 1) != 0)
        {
            return;
        }

        _writerTask = Task.Run(async () =>
        {
            try
            {
                Directory.CreateDirectory(AppMetadata.DiagnosticsDirectoryPath);
                await foreach (var firstLine in Queue.Reader.ReadAllAsync())
                {
                    var builder = new StringBuilder(firstLine.Length + 256)
                        .Append(firstLine)
                        .Append(Environment.NewLine);
                    var count = 1;
                    while (count < 128 && Queue.Reader.TryRead(out var line))
                    {
                        builder.Append(line).Append(Environment.NewLine);
                        count++;
                    }

                    lock (LogLock)
                    {
                        if (File.Exists(LogFilePath) &&
                            new FileInfo(LogFilePath).Length > MaxLogSizeBytes)
                        {
                            File.WriteAllText(LogFilePath, builder.ToString(), Encoding.UTF8);
                        }
                        else
                        {
                            File.AppendAllText(LogFilePath, builder.ToString(), Encoding.UTF8);
                        }
                    }
                }
            }
            catch
            {
                // Diagnostics must never affect startup or input processing.
            }
        });
    }

    public static void Flush()
    {
        if (!IsEnabled || Volatile.Read(ref _writerStarted) == 0)
        {
            return;
        }

        Queue.Writer.TryComplete();
        try
        {
            _writerTask?.Wait(TimeSpan.FromSeconds(1));
        }
        catch
        {
            // A diagnostic flush must never delay or fail application exit.
        }
    }
}
