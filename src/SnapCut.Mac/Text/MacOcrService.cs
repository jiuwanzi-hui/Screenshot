using System.Diagnostics;
using System.Text.Json;
using Avalonia;
using SnapCut.Core;
using SnapCut.Mac.Capture;

namespace SnapCut.Mac.Text;

internal sealed class MacOcrService
{
    private readonly MacOcrModelManager _models;

    public MacOcrService(MacOcrModelManager models)
    {
        _models = models;
    }

    public async Task<MacOcrRecognitionResult> RecognizeAsync(
        PixelImage image,
        CancellationToken cancellationToken = default)
    {
        if (OperatingSystem.IsMacOS())
        {
            var vision = await Task.Run(
                () => MacVisionOcrService.Recognize(image),
                cancellationToken);
            if (vision.IsSuccess)
            {
                return vision;
            }
        }

        if (!_models.GetStatus().IsInstalled)
        {
            return MacOcrRecognitionResult.Failure(
                "高质量识别模型尚未下载，请到“内容识别”设置中安装。");
        }

        var helperPath = ResolveHelperPath();
        if (!File.Exists(helperPath))
        {
            return MacOcrRecognitionResult.Failure(
                "OCR 辅助程序缺失，请重新安装完整的 SnapCut.app。");
        }

        var temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            $"SnapCut-Ocr-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
        var imagePath = Path.Combine(temporaryDirectory, "input.png");
        var resultPath = Path.Combine(temporaryDirectory, "result.json");
        try
        {
            MacScreenCaptureService.SavePng(image, imagePath);
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = helperPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                ArgumentList =
                {
                    "--image", imagePath,
                    "--models", _models.InstallationDirectory,
                    "--out", resultPath,
                },
            });
            if (process is null)
            {
                return MacOcrRecognitionResult.Failure("无法启动 OCR 辅助程序。");
            }

            await process.WaitForExitAsync(cancellationToken);
            if (process.ExitCode != 0 || !File.Exists(resultPath))
            {
                var detail = (await process.StandardError
                    .ReadToEndAsync(cancellationToken)).Trim();
                return MacOcrRecognitionResult.Failure(
                    string.IsNullOrWhiteSpace(detail)
                        ? "高质量识别运行失败。"
                        : $"高质量识别运行失败：{Limit(detail)}");
            }

            var result = JsonSerializer.Deserialize<HostResult>(
                await File.ReadAllTextAsync(resultPath, cancellationToken));
            return result is null
                ? MacOcrRecognitionResult.Failure("OCR 辅助程序返回了无效结果。")
                : ConvertResult(result);
        }
        catch (OperationCanceledException)
        {
            return MacOcrRecognitionResult.Failure("文字识别已取消。");
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return MacOcrRecognitionResult.Failure(
                $"高质量识别运行失败：{exception.Message}");
        }
        finally
        {
            try
            {
                Directory.Delete(temporaryDirectory, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static MacOcrRecognitionResult ConvertResult(HostResult result) =>
        new(result.IsSuccess, result.Text, result.ErrorMessage)
        {
            Regions = result.Regions.Select(region => new MacOcrTextRegion(
                region.Text,
                ToRect(region.Bounds),
                Math.Clamp(region.Bounds.Height / 1.12, 8, 64))).ToArray(),
            Words = result.Words.Select(word => new MacOcrWordRegion(
                word.Text,
                ToRect(word.Bounds))).ToArray(),
        };

    private static Rect ToRect(HostBounds bounds) => new(
        bounds.X,
        bounds.Y,
        bounds.Width,
        bounds.Height);

    private static string ResolveHelperPath()
    {
        var executableName = OperatingSystem.IsWindows()
            ? "snapcut-ocr.exe"
            : "snapcut-ocr";
        var packaged = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "Helpers",
            "OcrHost",
            executableName));
        if (File.Exists(packaged))
        {
            return packaged;
        }

        return Path.Combine(AppContext.BaseDirectory, executableName);
    }

    private static string Limit(string value) =>
        value.Length <= 300 ? value : value[..300] + "...";

    private sealed record HostBounds(double X, double Y, double Width, double Height);

    private sealed record HostRegion(string Text, HostBounds Bounds);

    private sealed record HostResult(
        bool IsSuccess,
        string Text,
        string? ErrorMessage,
        IReadOnlyList<HostRegion> Regions,
        IReadOnlyList<HostRegion> Words);
}
