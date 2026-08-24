using System.Runtime.InteropServices;
using Avalonia;
using SnapCut.Core;
using SnapCut.Mac.Capture;
using SnapCut.Mac.Native;

namespace SnapCut.Mac.Text;

internal static class MacVisionOcrService
{
    private const string VisionFramework =
        "/System/Library/Frameworks/Vision.framework/Vision";
    private static readonly IntPtr VisionHandle = LoadVision();

    public static MacOcrRecognitionResult Recognize(PixelImage image)
    {
        if (!OperatingSystem.IsMacOS() || VisionHandle == IntPtr.Zero)
        {
            return MacOcrRecognitionResult.Failure("当前系统无法加载 Vision OCR。");
        }

        var temporary = Path.Combine(
            Path.GetTempPath(),
            $"SnapCut-Vision-{Guid.NewGuid():N}.png");
        var request = IntPtr.Zero;
        var handler = IntPtr.Zero;
        var urlString = IntPtr.Zero;
        try
        {
            MacScreenCaptureService.SavePng(image, temporary);
            urlString = ObjectiveC.CreateString(temporary);
            var url = ObjectiveC.SendIntPtr(
                ObjectiveC.GetClass("NSURL"),
                "fileURLWithPath:",
                urlString);
            request = ObjectiveC.SendIntPtr(
                ObjectiveC.SendIntPtr(
                    ObjectiveC.GetClass("VNRecognizeTextRequest"),
                    "alloc"),
                "initWithCompletionHandler:",
                IntPtr.Zero);
            if (request == IntPtr.Zero)
            {
                return MacOcrRecognitionResult.Failure("无法创建 Vision 文字识别请求。");
            }

            ObjectiveC.SendVoid(request, "setRecognitionLevel:", new IntPtr(1));
            ObjectiveC.SendVoid(request, "setUsesLanguageCorrection:", new IntPtr(1));
            var options = ObjectiveC.SendIntPtr(
                ObjectiveC.GetClass("NSDictionary"),
                "dictionary");
            handler = ObjectiveC.SendIntPtr(
                ObjectiveC.SendIntPtr(
                    ObjectiveC.GetClass("VNImageRequestHandler"),
                    "alloc"),
                "initWithURL:options:",
                url,
                options);
            var requests = ObjectiveC.SendIntPtr(
                ObjectiveC.GetClass("NSArray"),
                "arrayWithObject:",
                request);
            var errorPointer = Marshal.AllocHGlobal(IntPtr.Size);
            try
            {
                Marshal.WriteIntPtr(errorPointer, IntPtr.Zero);
                if (!ObjectiveC.SendBool(
                        handler,
                        "performRequests:error:",
                        requests,
                        errorPointer))
                {
                    return MacOcrRecognitionResult.Failure(
                        "Vision OCR 执行失败，请确认图片有效。");
                }
            }
            finally
            {
                Marshal.FreeHGlobal(errorPointer);
            }

            return ReadResults(request, image.Width, image.Height);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                DllNotFoundException or EntryPointNotFoundException)
        {
            return MacOcrRecognitionResult.Failure(
                $"Vision OCR 运行失败：{exception.Message}");
        }
        finally
        {
            ObjectiveC.Release(handler);
            ObjectiveC.Release(request);
            ObjectiveC.Release(urlString);
            try
            {
                File.Delete(temporary);
            }
            catch (IOException)
            {
            }
        }
    }

    private static MacOcrRecognitionResult ReadResults(
        IntPtr request,
        int imageWidth,
        int imageHeight)
    {
        var results = ObjectiveC.SendIntPtr(request, "results");
        var count = (int)ObjectiveC.SendNInt(results, "count");
        var regions = new List<MacOcrTextRegion>(count);
        for (var index = 0; index < count; index++)
        {
            var observation = ObjectiveC.SendIntPtr(
                results,
                "objectAtIndex:",
                new IntPtr(index));
            var candidates = ObjectiveC.SendIntPtr(
                observation,
                "topCandidates:",
                new IntPtr(1));
            if (ObjectiveC.SendNInt(candidates, "count") == 0)
            {
                continue;
            }

            var candidate = ObjectiveC.SendIntPtr(
                candidates,
                "objectAtIndex:",
                IntPtr.Zero);
            var text = ObjectiveC.ReadString(
                ObjectiveC.SendIntPtr(candidate, "string"));
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            var normalized = ObjectiveC.SendCGRect(observation, "boundingBox");
            var bounds = new Rect(
                normalized.Left * imageWidth,
                (1 - normalized.Top - normalized.Size.Height) * imageHeight,
                normalized.Size.Width * imageWidth,
                normalized.Size.Height * imageHeight);
            regions.Add(new MacOcrTextRegion(
                text.Trim(),
                bounds,
                Math.Clamp(bounds.Height / 1.12, 8, 64)));
        }

        var ordered = regions.OrderBy(region => region.Bounds.Top)
            .ThenBy(region => region.Bounds.Left)
            .ToArray();
        return new MacOcrRecognitionResult(
            true,
            string.Join(Environment.NewLine, ordered.Select(region => region.Text)),
            null)
        {
            Regions = ordered,
            Words = ordered.Select(region => new MacOcrWordRegion(
                region.Text,
                region.Bounds)).ToArray(),
        };
    }

    private static IntPtr LoadVision() => OperatingSystem.IsMacOS()
        ? NativeLibrary.Load(VisionFramework)
        : IntPtr.Zero;
}
