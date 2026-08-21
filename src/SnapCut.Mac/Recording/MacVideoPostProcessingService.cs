using System.Diagnostics;
using System.Globalization;

namespace SnapCut.Mac.Recording;

internal enum MacVideoExportFormat
{
    Mp4,
    Gif,
    WebP,
}

internal static class MacVideoPostProcessingService
{
    public static async Task<double> ProbeDurationAsync(
        string input,
        CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(
            ResolveHelper("ffprobe"),
            [
                "-v", "error",
                "-show_entries", "format=duration",
                "-of", "default=noprint_wrappers=1:nokey=1",
                input,
            ],
            cancellationToken);
        return result.ExitCode == 0 && double.TryParse(
            result.StandardOutput.Trim(),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var duration)
            ? duration
            : 0;
    }

    public static async Task<bool> ExtractFrameAsync(
        string input,
        double seconds,
        string output,
        CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(
            ResolveHelper("ffmpeg"),
            [
                "-y", "-ss", seconds.ToString("F3", CultureInfo.InvariantCulture),
                "-i", input,
                "-frames:v", "1",
                "-vf", "scale='min(960,iw)':-2:flags=lanczos",
                output,
            ],
            cancellationToken);
        return result.ExitCode == 0 && File.Exists(output);
    }

    public static async Task<(bool IsSuccess, string? Error)> ExportAsync(
        string input,
        double start,
        double end,
        string output,
        MacVideoExportFormat format,
        string codec,
        int frameRate,
        CancellationToken cancellationToken = default)
    {
        var duration = Math.Max(0.05, end - start);
        var arguments = new List<string>
        {
            "-y",
            "-ss", start.ToString("F3", CultureInfo.InvariantCulture),
            "-t", duration.ToString("F3", CultureInfo.InvariantCulture),
            "-i", input,
        };
        switch (format)
        {
            case MacVideoExportFormat.Gif:
                arguments.AddRange([
                    "-vf",
                    $"fps={frameRate},scale='min(1280,iw)':-2:flags=lanczos,split[s0][s1];[s0]palettegen[p];[s1][p]paletteuse",
                    "-loop", "0",
                ]);
                break;
            case MacVideoExportFormat.WebP:
                arguments.AddRange([
                    "-vf", $"fps={frameRate},scale='min(1280,iw)':-2:flags=lanczos",
                    "-loop", "0",
                    "-c:v", "libwebp_anim",
                    "-quality", "80",
                ]);
                break;
            default:
                arguments.AddRange([
                    "-c:v", codec == "H265" ? "libx265" : "libx264",
                    "-crf", codec == "H265" ? "28" : "23",
                    "-preset", "medium",
                    "-r", frameRate.ToString(CultureInfo.InvariantCulture),
                    "-pix_fmt", "yuv420p",
                    "-c:a", "aac",
                    "-movflags", "+faststart",
                ]);
                break;
        }
        arguments.Add(output);
        var result = await RunAsync(
            ResolveHelper("ffmpeg"),
            arguments,
            cancellationToken);
        if (result.ExitCode == 0 && File.Exists(output))
        {
            return (true, null);
        }

        var detail = result.StandardError.Trim();
        return (false, string.IsNullOrWhiteSpace(detail)
            ? "视频处理失败。"
            : detail.Length <= 300 ? detail : detail[^300..]);
    }

    private static async Task<ProcessResult> RunAsync(
        string executable,
        IEnumerable<string> arguments,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(executable))
        {
            return new ProcessResult(-1, string.Empty, "辅助程序缺失：" + executable);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            return new ProcessResult(-1, string.Empty, "无法启动辅助程序。");
        }

        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return new ProcessResult(process.ExitCode, await stdout, await stderr);
    }

    private static string ResolveHelper(string name) => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory,
        "..",
        "Helpers",
        name));

    private sealed record ProcessResult(
        int ExitCode,
        string StandardOutput,
        string StandardError);
}
