using System.Text.Json;
using RapidOcrNet;
using SkiaSharp;

namespace SnapCut.Mac.OcrHost;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            var imagePath = GetOption(args, "--image");
            var modelDirectory = GetOption(args, "--models");
            var outputPath = GetOption(args, "--out");
            if (imagePath is null || modelDirectory is null || outputPath is null)
            {
                Console.Error.WriteLine("Usage: snapcut-ocr --image input.png --models directory --out result.json");
                return 2;
            }

            using var bitmap = SKBitmap.Decode(imagePath)
                ?? throw new InvalidDataException("无法读取待识别图片。");
            using var engine = new RapidOcr();
            engine.InitModels(
                RapidOcrModelSet.PPOCRv6Small with
                {
                    DetModelPath = Path.Combine(modelDirectory, "PP-OCRv6_det_small.onnx"),
                    RecModelPath = Path.Combine(modelDirectory, "PP-OCRv6_rec_small.onnx"),
                    ClsModelPath = Path.Combine(
                        modelDirectory,
                        "ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx"),
                    KeysPath = Path.Combine(modelDirectory, "ppocrv6_dict.txt"),
                },
                Math.Max(1, Environment.ProcessorCount / 2));
            var detected = engine.Detect(bitmap, RapidOcrOptions.PPOCRv6 with
            {
                ReturnWordBox = true,
                TextScore = 0.45f,
            });
            var blocks = detected.TextBlocks
                .Where(block => !string.IsNullOrWhiteSpace(block.Text))
                .OrderBy(block => block.BoxPoints.Min(point => point.Y))
                .ThenBy(block => block.BoxPoints.Min(point => point.X))
                .ToArray();
            var result = new HostResult(
                true,
                string.Join(Environment.NewLine, blocks.Select(block => block.Text)),
                null,
                blocks.Select(block => new HostRegion(
                    block.Text.Trim(),
                    Bounds(block.BoxPoints))).ToArray(),
                blocks.SelectMany(block => block.WordResults is { Length: > 0 }
                    ? block.WordResults.Select(word => new HostRegion(
                        word.Text.Trim(),
                        Bounds(word.BoxPoints)))
                    : [new HostRegion(block.Text.Trim(), Bounds(block.BoxPoints))])
                    .Where(region => !string.IsNullOrWhiteSpace(region.Text))
                    .ToArray());
            File.WriteAllText(outputPath, JsonSerializer.Serialize(result));
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static HostBounds Bounds(IReadOnlyList<SKPointI> points)
    {
        var left = points.Min(point => point.X);
        var top = points.Min(point => point.Y);
        var right = points.Max(point => point.X);
        var bottom = points.Max(point => point.Y);
        return new HostBounds(
            left,
            top,
            Math.Max(1, right - left),
            Math.Max(1, bottom - top));
    }

    private static string? GetOption(string[] args, string name)
    {
        for (var index = 0; index < args.Length - 1; index++)
        {
            if (args[index] == name)
            {
                return args[index + 1];
            }
        }

        return null;
    }

    private sealed record HostBounds(double X, double Y, double Width, double Height);

    private sealed record HostRegion(string Text, HostBounds Bounds);

    private sealed record HostResult(
        bool IsSuccess,
        string Text,
        string? ErrorMessage,
        IReadOnlyList<HostRegion> Regions,
        IReadOnlyList<HostRegion> Words);
}
