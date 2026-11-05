using System.Diagnostics;
using System.Drawing;
using Screenshot.App.Capture;
using Screenshot.App.Text;
using Xunit.Abstractions;

namespace Screenshot.App.Tests;

public sealed class TableCellOcrTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(256000)]
    [InlineData(1960000)]
    public void CellBatchesPreserveResolutionAndSeparateAllCells(int budget)
    {
        var bounds = Enumerable.Range(0, 100).Select(index => new Rectangle(index % 4 * 220, index / 4 * 55, 220, 55)).ToArray();
        var batches = TableSupplementaryOcrService.PlanCellBatches(bounds, [], budget);
        Assert.Equal(bounds.Length, batches.Sum(batch => batch.Count));
        Assert.All(batches, batch =>
        {
            Assert.InRange(batch.Max(cell => cell.Destination.Right) + 12, 1, 1400);
            Assert.InRange(batch.Max(cell => cell.Destination.Bottom) + 12, 1, 1400);
            Assert.True((long)(batch.Max(cell => cell.Destination.Right) + 12) * (batch.Max(cell => cell.Destination.Bottom) + 12) <= budget);
            Assert.All(batch, cell =>
            {
                Assert.True(cell.Destination.Width >= cell.Source.Width && cell.Destination.Height >= cell.Source.Height);
                Assert.DoesNotContain(batch, other => !ReferenceEquals(cell, other) && cell.Destination.IntersectsWith(other.Destination));
            });
        });
    }

    [TableOcrFact]
    public async Task RealOcrRestoresOpenSidedMergedTableTextAndStructure()
    {
        using var manager = new HighQualityOcrModelManager(Environment.GetEnvironmentVariable("SNAPCUT_OCR_MODEL_DIRECTORY"));
        using var image = CreateTable();
        for (var run = 0; run < 2; run++)
        {
            var watch = Stopwatch.StartNew();
            var primary = await HighQualityOcrService.RecognizeAsync(image, manager);
            Assert.True(primary.IsSuccess, primary.ErrorMessage);
            var supplementary = await TableSupplementaryOcrService.RecognizeAsync(image, primary.Words, manager);
            var table = TableRecognitionService.BuildTsv(primary, image.Bitmap, supplementary);
            output.WriteLine($"run={run}; total={watch.ElapsedMilliseconds} ms; table={table.Content}; primary={primary.Text}");
            Assert.True(table.IsSuccess, table.ErrorMessage);
            Assert.Equal(4, table.Content.Split(Environment.NewLine).Length);
            Assert.Contains("colspan=\"2\"", table.ClipboardHtml);
            Assert.Contains("项目", table.Content);
            Assert.Contains("负责人", table.Content);
            Assert.Contains("120", table.Content);
            Assert.Contains("王明", table.Content);
            Assert.Contains("设备检查", table.Content);
            Assert.Contains("已完成", table.Content);
        }
        var worker = (Process)typeof(OcrWorkerProcess).GetField("_process",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null)!;
        using var observer = Process.GetProcessById(worker.Id);
        observer.Refresh();
        output.WriteLine($"OCR worker peak working set={observer.PeakWorkingSet64 / 1048576d:F1} MiB; private bytes={observer.PrivateMemorySize64 / 1048576d:F1} MiB.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await observer.WaitForExitAsync(timeout.Token);
        output.WriteLine("Table OCR worker exited after the idle deadline.");
    }

    [TableOcrFact]
    public async Task CancellationDoesNotKeepTheSourceOrPreventTheNextRecognition()
    {
        using var manager = new HighQualityOcrModelManager(Environment.GetEnvironmentVariable("SNAPCUT_OCR_MODEL_DIRECTORY"));
        using var image = CreateTable();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => TableSupplementaryOcrService.RecognizeAsync(image, [], manager, cancellation.Token));
        var result = await HighQualityOcrService.RecognizeAsync(image, manager);
        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Contains("120", result.Text);
    }

    private static CapturedImage CreateTable()
    {
        var image = new CapturedImage(new Bitmap(700, 270));
        using var graphics = Graphics.FromImage(image.Bitmap);
        using var font = new Font("Microsoft YaHei UI", 19, FontStyle.Regular, GraphicsUnit.Pixel);
        graphics.Clear(Color.White);
        foreach (var y in new[] { 10, 65, 120, 175, 230 }) graphics.DrawLine(Pens.Gray, 10, y, 690, y);
        graphics.DrawLine(Pens.Gray, 180, 65, 180, 230);
        graphics.DrawLine(Pens.Gray, 350, 10, 350, 230);
        graphics.DrawLine(Pens.Gray, 520, 10, 520, 230);
        foreach (var (text, x, y) in new[] { ("设备检查", 120, 25), ("数量", 400, 25), ("负责人", 555, 25),
            ("项目", 45, 80), ("状态", 210, 80), ("120", 400, 80), ("王明", 565, 80),
            ("安装", 45, 135), ("已完成", 210, 135), ("240", 400, 135), ("李华", 565, 135),
            ("检查", 45, 190), ("正常", 210, 190), ("360", 400, 190), ("张强", 565, 190) })
            graphics.DrawString(text, font, Brushes.Black, x, y);
        return image;
    }

    private sealed class TableOcrFactAttribute : FactAttribute
    {
        public TableOcrFactAttribute()
        {
            if (!System.IO.Directory.Exists(Environment.GetEnvironmentVariable("SNAPCUT_OCR_MODEL_DIRECTORY")))
                Skip = "Set SNAPCUT_OCR_MODEL_DIRECTORY for native OCR; uses offscreen generated tables only.";
        }
    }
}
