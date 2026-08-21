using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using SnapCut.Mac.App;
using SnapCut.Mac.Native;

namespace SnapCut.Mac.Recording;

internal sealed record MacVideoRecordingResult(
    bool IsSuccess,
    string? OutputPath,
    bool OpenEditor,
    string? ErrorMessage);

internal static class MacVideoRecordingService
{
    private const int InterruptSignal = 2;

    public static async Task<MacVideoRecordingResult> RecordAsync(
        CGRect region,
        MacSettings settings,
        CancellationToken cancellationToken = default)
    {
        var videoDirectory = string.IsNullOrWhiteSpace(settings.VideoSaveDirectory)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Movies",
                "SnapCut")
            : settings.VideoSaveDirectory;
        Directory.CreateDirectory(videoDirectory);
        var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
        var temporary = Path.Combine(videoDirectory, $"SnapCut-{timestamp}.mov");
        var arguments = new List<string>
        {
            "-v",
            "-R" + string.Join(",",
                Math.Round(region.Left).ToString(CultureInfo.InvariantCulture),
                Math.Round(region.Top).ToString(CultureInfo.InvariantCulture),
                Math.Round(region.Size.Width).ToString(CultureInfo.InvariantCulture),
                Math.Round(region.Size.Height).ToString(CultureInfo.InvariantCulture)),
        };
        if (settings.RecordMicrophone || settings.RecordSystemAudio)
        {
            arguments.Add("-g");
        }
        if (settings.ShowMouseInputInRecording)
        {
            arguments.Add("-k");
        }
        arguments.Add(temporary);

        using var inputMonitor = new MacRecordingInputMonitor(
            settings.ShowKeyboardInputInRecording,
            settings.ShowMouseInputInRecording);
        MacRecordingInputOverlayWindow? inputOverlay = null;
        if (settings.ShowKeyboardInputInRecording || settings.ShowMouseInputInRecording)
        {
            inputOverlay = new MacRecordingInputOverlayWindow(region);
            inputMonitor.KeyPressed += inputOverlay.ShowKey;
            inputMonitor.MousePressed += inputOverlay.ShowMouse;
            _ = inputMonitor.Start();
        }

        using var recorder = StartProcess("/usr/sbin/screencapture", arguments);
        if (recorder is null)
        {
            inputOverlay?.Close();
            return new MacVideoRecordingResult(
                false, null, false, "无法启动 macOS 屏幕录制程序。");
        }

        var completion = await new MacRecordingControlWindow().WaitAsync();
        if (!recorder.HasExited)
        {
            _ = Kill(recorder.Id, InterruptSignal);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                timeout.Token);
            try
            {
                await recorder.WaitForExitAsync(linked.Token);
            }
            catch (OperationCanceledException)
            {
                recorder.Kill(entireProcessTree: true);
            }
        }
        inputOverlay?.Close();

        if (completion == MacRecordingCompletion.Cancel)
        {
            TryDelete(temporary);
            return new MacVideoRecordingResult(false, null, false, "录制已取消。");
        }

        if (!File.Exists(temporary) || new FileInfo(temporary).Length == 0)
        {
            return new MacVideoRecordingResult(
                false, null, false, "录制文件没有生成，请检查屏幕录制权限。");
        }

        var format = settings.VideoOutputFormat == "Gif" ? "gif" : "mp4";
        var output = Path.Combine(videoDirectory, $"SnapCut-{timestamp}.{format}");
        var converted = await ConvertAsync(temporary, output, settings, cancellationToken);
        if (!converted.IsSuccess)
        {
            return converted with
            {
                OpenEditor = completion == MacRecordingCompletion.StopAndEdit,
            };
        }

        TryDelete(temporary);
        return converted with
        {
            OpenEditor = completion == MacRecordingCompletion.StopAndEdit,
        };
    }

    private static async Task<MacVideoRecordingResult> ConvertAsync(
        string input,
        string output,
        MacSettings settings,
        CancellationToken cancellationToken)
    {
        var ffmpeg = ResolveFfmpegPath();
        if (!File.Exists(ffmpeg))
        {
            return new MacVideoRecordingResult(
                false,
                input,
                false,
                "FFmpeg 辅助程序缺失，已保留 MOV 原始文件。");
        }

        var args = new List<string> { "-y", "-i", input };
        if (settings.VideoOutputFormat == "Gif")
        {
            args.AddRange([
                "-vf",
                $"fps={settings.VideoFrameRate},scale='min(1280,iw)':-2:flags=lanczos,split[s0][s1];[s0]palettegen[p];[s1][p]paletteuse",
                "-loop", "0",
            ]);
        }
        else
        {
            args.AddRange([
                "-c:v", settings.VideoCodec == "H265" ? "libx265" : "libx264",
                "-preset", "medium",
                "-crf", settings.VideoCodec == "H265" ? "28" : "23",
                "-r", settings.VideoFrameRate.ToString(CultureInfo.InvariantCulture),
                "-pix_fmt", "yuv420p",
                "-c:a", "aac",
                "-movflags", "+faststart",
            ]);
        }
        args.Add(output);
        using var process = StartProcess(ffmpeg, args, redirectError: true);
        if (process is null)
        {
            return new MacVideoRecordingResult(false, input, false, "无法启动 FFmpeg。");
        }

        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0 || !File.Exists(output))
        {
            var detail = (await process.StandardError.ReadToEndAsync(cancellationToken)).Trim();
            return new MacVideoRecordingResult(
                false,
                input,
                false,
                string.IsNullOrWhiteSpace(detail)
                    ? "视频格式转换失败，已保留 MOV 原始文件。"
                    : "视频格式转换失败：" +
                      (detail.Length <= 300 ? detail : detail[^300..]));
        }

        return new MacVideoRecordingResult(true, output, false, null);
    }

    private static Process? StartProcess(
        string fileName,
        IEnumerable<string> arguments,
        bool redirectError = false)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = redirectError,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return Process.Start(startInfo);
    }

    private static string ResolveFfmpegPath() => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory,
        "..",
        "Helpers",
        "ffmpeg"));

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [DllImport("/usr/lib/libSystem.B.dylib")]
    private static extern int Kill(int processIdentifier, int signal);
}
