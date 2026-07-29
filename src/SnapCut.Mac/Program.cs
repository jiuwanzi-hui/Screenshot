using System.Globalization;
using SnapCut.Core;
using SnapCut.Mac.Capture;
using SnapCut.Mac.Native;

namespace SnapCut.Mac;

/// <summary>
/// SnapCut macOS 命令行原型：验证共享拼接核心在 macOS 抓屏/滚轮链路上的行为。
/// 后续的菜单栏 App 会复用这里的捕获与引擎层。
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintUsage();
            return 0;
        }

        if (!OperatingSystem.IsMacOS())
        {
            Console.Error.WriteLine("snapcut(Mac 前端)只能在 macOS 上运行。");
            return 1;
        }

        try
        {
            return args[0] switch
            {
                "displays" => RunDisplays(),
                "capture" => RunCapture(args),
                "scroll" => RunScroll(args),
                "permissions" => RunPermissions(),
                _ => Unknown(args[0]),
            };
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"错误：{exception.Message}");
            return 1;
        }
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"未知命令：{command}");
        PrintUsage();
        return 2;
    }

    private static void PrintUsage()
    {
        Console.WriteLine(
            """
            SnapCut macOS 原型

            用法:
              snapcut displays                            列出显示器（点坐标边界、像素尺寸、缩放）
              snapcut permissions                         检查/申请屏幕录制权限
              snapcut capture --rect X,Y,W,H --out a.png  截取全局坐标区域（点）为 PNG
              snapcut capture --out a.png                 截取主显示器
              snapcut scroll [--rect X,Y,W,H] --out a.png 滚动长截图：启动后滚动目标区域，
                                                          按 Enter 结束并保存；
                                                          不带 --rect 时用主屏中央 60% 区域

            权限:
              抓屏需要「屏幕录制」权限；滚轮监听需要「输入监控」权限
              （系统设置 → 隐私与安全性）。缺少滚轮权限时长截图仍可用，
              方向完全由图像证据决定。
            """);
    }

    private static int RunDisplays()
    {
        foreach (var display in MacDisplayService.GetActiveDisplays())
        {
            Console.WriteLine(
                $"#{display.DisplayId}{(display.IsMain ? " (主屏)" : string.Empty)} " +
                $"bounds=({display.Bounds.Left:F0},{display.Bounds.Top:F0}," +
                $"{display.Bounds.Size.Width:F0}x{display.Bounds.Size.Height:F0}) " +
                $"pixels={display.PixelWidth}x{display.PixelHeight} " +
                $"scale={display.Scale:F2}");
        }

        return 0;
    }

    private static int RunPermissions()
    {
        if (MacScreenCaptureService.HasScreenCaptureAccess())
        {
            Console.WriteLine("屏幕录制权限：已授予。");
            return 0;
        }

        Console.WriteLine("屏幕录制权限：未授予，正在向系统申请…");
        var granted = MacScreenCaptureService.RequestScreenCaptureAccess();
        Console.WriteLine(granted
            ? "已授予。"
            : "仍未授予。请在 系统设置 → 隐私与安全性 → 屏幕录制 中勾选本程序后重试。");
        return granted ? 0 : 1;
    }

    private static int RunCapture(string[] args)
    {
        var rect = ParseRectOption(args) ?? MainDisplayRect();
        var output = ParseOption(args, "--out") ?? "snapcut-capture.png";

        EnsureScreenCaptureAccess();
        var image = MacScreenCaptureService.CaptureRegion(rect);
        MacScreenCaptureService.SavePng(image, output);
        Console.WriteLine(
            $"已保存 {image.Width}x{image.Height} → {Path.GetFullPath(output)}");
        return 0;
    }

    private static int RunScroll(string[] args)
    {
        var rect = ParseRectOption(args) ?? CenterOfMainDisplay();
        var output = ParseOption(args, "--out") ?? "snapcut-scroll.png";

        EnsureScreenCaptureAccess();
        Console.WriteLine(
            $"采集区域（点坐标）：({rect.Left:F0},{rect.Top:F0}) " +
            $"{rect.Size.Width:F0}x{rect.Size.Height:F0}；" +
            "可用 --rect X,Y,W,H 自定义。");
        Console.WriteLine("滚动采集已开始：请滚动该区域内的内容，按 Enter 结束…");

        using var cancellation = new CancellationTokenSource();
        var input = Task.Run(() =>
        {
            Console.ReadLine();
            cancellation.Cancel();
        });

        var engine = new ScrollCaptureEngine(ScrollCaptureOptions.Default);
        var lastReport = 0;
        var image = engine.Run(
            rect,
            cancellation.Token,
            progress =>
            {
                if (progress.OutputHeight - lastReport >= 200)
                {
                    lastReport = progress.OutputHeight;
                    Console.WriteLine(
                        $"  已拼接 {progress.StitchedFrames} 段，" +
                        $"高度 {progress.OutputHeight}px，积压 {progress.BacklogCount}");
                }
            });

        MacScreenCaptureService.SavePng(image, output);
        Console.WriteLine(
            $"已保存 {image.Width}x{image.Height} → {Path.GetFullPath(output)}");
        return 0;
    }

    private static void EnsureScreenCaptureAccess()
    {
        if (!MacScreenCaptureService.HasScreenCaptureAccess() &&
            !MacScreenCaptureService.RequestScreenCaptureAccess())
        {
            throw new InvalidOperationException(
                "缺少屏幕录制权限。请在 系统设置 → 隐私与安全性 → 屏幕录制 中授权后重试。");
        }
    }

    private static CGRect MainDisplayRect()
    {
        var display = MacDisplayService.GetActiveDisplays()
            .First(candidate => candidate.IsMain);
        return display.Bounds;
    }

    /// <summary>
    /// 主屏中央约 60% 的区域：让 scroll 不带 --rect 也能直接测试，
    /// 同时避开菜单栏和 Dock。
    /// </summary>
    private static CGRect CenterOfMainDisplay()
    {
        var bounds = MainDisplayRect();
        var insetX = bounds.Size.Width * 0.2;
        var insetY = bounds.Size.Height * 0.2;
        return new CGRect(
            bounds.Left + insetX,
            bounds.Top + insetY,
            bounds.Size.Width - (insetX * 2),
            bounds.Size.Height - (insetY * 2));
    }

    private static CGRect? ParseRectOption(string[] args)
    {
        var value = ParseOption(args, "--rect");

        if (value is null)
        {
            return null;
        }

        var parts = value.Split(',');

        if (parts.Length != 4 ||
            !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ||
            !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y) ||
            !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var width) ||
            !double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var height) ||
            width < 1 ||
            height < 1)
        {
            throw new ArgumentException("--rect 需要 X,Y,W,H 四个数（全局点坐标）。");
        }

        return new CGRect(x, y, width, height);
    }

    private static string? ParseOption(string[] args, string name)
    {
        for (var index = 1; index < args.Length - 1; index++)
        {
            if (args[index] == name)
            {
                return args[index + 1];
            }
        }

        return null;
    }
}
