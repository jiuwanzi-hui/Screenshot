using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using Screenshot.App.Core;
using Screenshot.App.Capture;
using Screenshot.App.Editor;
using Screenshot.App.Text;
using ZXing;
using ZXing.Common;

namespace Screenshot.App.Tests;

public sealed class ContentRecognitionServiceTests
{
    [Fact]
    public void ResultWindowUsesRoundedShellAndDisplaysContent()
    {
        WpfTestHost.Invoke(() =>
        {
            var window = new ContentRecognitionWindow(new ContentRecognitionResult(
                true,
                "二维码",
                "https://example.com"));
            try
            {
                window.Show();
                window.UpdateLayout();
                var shell = Assert.IsType<System.Windows.Controls.Border>(
                    window.Content);
                Assert.Equal(new System.Windows.CornerRadius(12), shell.CornerRadius);
                Assert.Contains(
                    "https://example.com",
                    Assert.IsType<System.Windows.Controls.TextBox>(
                        window.FindName("ResultTextBox")).Text);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void RecognizesQrCodeLocally()
    {
        const string expected = "https://gitee.com/wwangyunhui/screenshot";
        var writer = new BarcodeWriterPixelData
        {
            Format = BarcodeFormat.QR_CODE,
            Options = new EncodingOptions
            {
                Width = 280,
                Height = 280,
                Margin = 3,
            },
        };
        var pixels = writer.Write(expected);
        using var bitmap = new Bitmap(
            pixels.Width,
            pixels.Height,
            PixelFormat.Format32bppArgb);
        var data = bitmap.LockBits(
            new Rectangle(0, 0, bitmap.Width, bitmap.Height),
            ImageLockMode.WriteOnly,
            PixelFormat.Format32bppArgb);
        try
        {
            Marshal.Copy(pixels.Pixels, 0, data.Scan0, pixels.Pixels.Length);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        using var image = new CapturedImage((Bitmap)bitmap.Clone());
        var result = QrCodeRecognitionService.Recognize(image);

        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Equal(expected, result.Content);
        Assert.NotNull(result.Region);
        Assert.InRange(result.Region.CenterX, 90, 190);
        Assert.InRange(result.Region.CenterY, 90, 190);
    }

    [Fact]
    public void ReconstructsAlignedOcrWordsAsTabSeparatedTable()
    {
        var ocr = new OcrRecognitionResult(true, "姓名 年龄\n张三 28", null)
        {
            Words =
            [
                new OcrWordRegion("姓名", 10, 10, 32, 18),
                new OcrWordRegion("年龄", 150, 10, 32, 18),
                new OcrWordRegion("张三", 10, 42, 32, 18),
                new OcrWordRegion("28", 150, 42, 24, 18),
            ],
        };

        var result = TableRecognitionService.BuildTsv(ocr);

        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Equal($"姓名\t年龄{Environment.NewLine}张三\t28", result.Content);
    }

    [Fact]
    public void TableRecognitionExplainsWhenColumnsCannotBeFound()
    {
        var ocr = new OcrRecognitionResult(true, "plain text", null)
        {
            Words =
            [
                new OcrWordRegion("plain", 10, 10, 40, 18),
                new OcrWordRegion("text", 58, 10, 34, 18),
            ],
        };

        var result = TableRecognitionService.BuildTsv(ocr);

        Assert.False(result.IsSuccess);
        Assert.Contains("表格", result.ErrorMessage);
    }

    [Fact]
    public void TableRecognitionKeepsSplitChinesePhrasesInOneCell()
    {
        var ocr = new OcrRecognitionResult(true, "", null)
        {
            Words =
            [
                new OcrWordRegion("名称", 10, 10, 34, 18),
                new OcrWordRegion("状态", 170, 10, 34, 18),
                new OcrWordRegion("登录", 10, 42, 34, 18),
                new OcrWordRegion("模板", 49, 42, 34, 18),
                new OcrWordRegion("+已启用", 170, 42, 66, 18),
                new OcrWordRegion("访客", 10, 74, 34, 18),
                new OcrWordRegion("模板", 49, 74, 34, 18),
                new OcrWordRegion("否", 170, 74, 18, 18),
            ],
        };

        var result = TableRecognitionService.BuildTsv(ocr);

        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Equal(
            $"名称\t状态{Environment.NewLine}" +
            $"登录模板\t'+已启用{Environment.NewLine}" +
            "访客模板\t否",
            result.Content);
    }

    [Fact]
    public void TableRecognitionKeepsCharacterBoxesInTheirLeftAlignedCells()
    {
        var ocr = new OcrRecognitionResult(true, string.Empty, null)
        {
            Words =
            [
                new OcrWordRegion("平台", 10, 10, 36, 20),
                new OcrWordRegion("实现方", 120, 10, 54, 20),
                new OcrWordRegion("优点", 300, 10, 36, 20),
                new OcrWordRegion("短板", 600, 10, 36, 20),
                new OcrWordRegion("安", 10, 42, 18, 20),
                new OcrWordRegion("卓", 28, 42, 18, 20),
                new OcrWordRegion("R", 50, 42, 10, 20),
                new OcrWordRegion("O", 60, 42, 10, 20),
                new OcrWordRegion("M", 70, 42, 11, 20),
                new OcrWordRegion("手机系统", 120, 42, 72, 20),
                new OcrWordRegion("一", 300, 42, 18, 20),
                new OcrWordRegion("键", 318, 42, 18, 20),
                new OcrWordRegion("连", 336, 42, 18, 20),
                new OcrWordRegion("续", 354, 42, 18, 20),
                new OcrWordRegion("长", 372, 42, 18, 20),
                new OcrWordRegion("内", 390, 42, 18, 20),
                new OcrWordRegion("容", 408, 42, 18, 20),
                new OcrWordRegion("操", 426, 42, 18, 20),
                new OcrWordRegion("作", 444, 42, 18, 20),
                new OcrWordRegion("无短板", 600, 42, 54, 20),
                new OcrWordRegion("iOS", 10, 74, 30, 20),
                new OcrWordRegion("系统", 120, 74, 36, 20),
                new OcrWordRegion("+", 164, 74, 12, 20),
                new OcrWordRegion("微信", 184, 74, 36, 20),
                new OcrWordRegion("画质安全", 300, 74, 72, 20),
                new OcrWordRegion("需分段", 600, 74, 54, 20),
            ],
        };

        var result = TableRecognitionService.BuildTsv(ocr);

        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Equal(
            $"平台\t实现方\t优点\t短板{Environment.NewLine}" +
            $"安卓 ROM\t手机系统\t一键连续长内容操作\t无短板{Environment.NewLine}" +
            "iOS\t系统 + 微信\t画质安全\t需分段",
            result.Content);
    }

    [Fact]
    public void DoesNotTreatOrdinaryTwoColumnTextAsATableWithoutGridLines()
    {
        using var image = new Bitmap(320, 160);
        using (var graphics = Graphics.FromImage(image))
        {
            graphics.Clear(Color.White);
        }

        var ocr = new OcrRecognitionResult(true, "", null)
        {
            Words =
            [
                new OcrWordRegion("左侧说明", 10, 10, 70, 18),
                new OcrWordRegion("右侧说明", 190, 10, 70, 18),
                new OcrWordRegion("第二行", 10, 42, 54, 18),
                new OcrWordRegion("另一区域", 190, 42, 70, 18),
                new OcrWordRegion("第三行", 10, 74, 54, 18),
                new OcrWordRegion("普通文字", 190, 74, 70, 18),
            ],
        };

        var result = TableRecognitionService.BuildTsv(ocr, image);

        Assert.False(result.IsSuccess);
        Assert.Contains("双栏", result.ErrorMessage);
    }

    [Fact]
    public void GridTablePreservesBlankCellsMergedCellsAndBackgroundColors()
    {
        using var image = new Bitmap(301, 151);
        using (var graphics = Graphics.FromImage(image))
        {
            graphics.Clear(Color.White);
            using var headerBrush = new SolidBrush(Color.FromArgb(0, 120, 200));
            using var statusBrush = new SolidBrush(Color.Yellow);
            using var gridPen = new Pen(Color.FromArgb(180, 190, 198));
            graphics.FillRectangle(headerBrush, 5, 5, 290, 40);
            graphics.FillRectangle(statusBrush, 185, 45, 60, 40);
            graphics.DrawRectangle(gridPen, 5, 5, 290, 140);
            graphics.DrawLine(gridPen, 5, 45, 295, 45);
            graphics.DrawLine(gridPen, 65, 85, 295, 85);
            graphics.DrawLine(gridPen, 65, 5, 65, 145);
            graphics.DrawLine(gridPen, 125, 5, 125, 45);
            graphics.DrawLine(gridPen, 125, 85, 125, 145);
            graphics.DrawLine(gridPen, 185, 5, 185, 145);
            graphics.DrawLine(gridPen, 245, 5, 245, 145);
        }

        var ocr = new OcrRecognitionResult(true, string.Empty, null)
        {
            Words =
            [
                new OcrWordRegion("项目", 15, 15, 30, 18),
                new OcrWordRegion("计划", 78, 15, 30, 18),
                new OcrWordRegion("状态", 198, 15, 30, 18),
                new OcrWordRegion("场地", 15, 55, 30, 18),
                new OcrWordRegion("合并内容", 78, 55, 72, 18),
                new OcrWordRegion("进行中", 194, 55, 42, 18),
                new OcrWordRegion("空白右侧", 252, 95, 36, 18),
            ],
        };

        var result = TableRecognitionService.BuildTsv(ocr, image);

        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Contains("colspan=\"2\"", result.ClipboardHtml);
        Assert.Contains("rowspan=\"2\"", result.ClipboardHtml);
        Assert.Contains("background-color:#0078C8", result.ClipboardHtml);
        Assert.Contains("background-color:#FFFF00", result.ClipboardHtml);
        Assert.Contains("<td", result.ClipboardHtml);
    }

    [Fact]
    public void GridTableIgnoresDistributedTextStrokesAndKeepsPartialBorders()
    {
        using var image = new Bitmap(401, 241);
        using (var graphics = Graphics.FromImage(image))
        {
            graphics.Clear(Color.White);
            using var gridPen = new Pen(Color.FromArgb(180, 190, 198));
            using var textStrokePen = new Pen(Color.FromArgb(20, 30, 40));
            foreach (var y in new[] { 5, 55, 175, 235 })
            {
                graphics.DrawLine(gridPen, 5, y, 395, y);
            }
            graphics.DrawLine(gridPen, 55, 115, 395, 115);
            foreach (var x in new[] { 5, 55, 105, 235, 395 })
            {
                graphics.DrawLine(gridPen, x, 5, x, 235);
            }
            graphics.DrawLine(gridPen, 155, 5, 155, 55);
            graphics.DrawLine(gridPen, 155, 175, 155, 235);
            graphics.DrawLine(gridPen, 315, 5, 315, 115);
            graphics.DrawLine(gridPen, 315, 175, 315, 235);

            // Many aligned short strokes have a high total edge count but are
            // not a table border because no individual stroke is continuous.
            for (var x = 12; x < 390; x += 18)
            {
                graphics.DrawLine(textStrokePen, x, 85, x + 7, 85);
            }
        }

        var ocr = new OcrRecognitionResult(true, string.Empty, null)
        {
            Words =
            [
                new OcrWordRegion("项目", 12, 16, 28, 18),
                new OcrWordRegion("开始", 65, 16, 28, 18),
                new OcrWordRegion("计划", 170, 16, 28, 18),
                new OcrWordRegion("场地", 12, 72, 28, 18),
                new OcrWordRegion("阶段一", 120, 72, 42, 18),
                new OcrWordRegion("横向说明文字", 12, 72, 376, 18),
                new OcrWordRegion("阶段二", 250, 132, 42, 18),
                new OcrWordRegion("设备", 12, 192, 28, 18),
            ],
        };

        var result = TableRecognitionService.BuildTsv(ocr, image);

        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Equal(4, Regex.Matches(result.ClipboardHtml!, "<tr\\b").Count);
        Assert.Contains("rowspan=\"2\"", result.ClipboardHtml);
        Assert.Contains("colspan=", result.ClipboardHtml);
    }

    [Fact]
    public void GridTableRestoresFirstRowAndColumnAtCaptureBoundary()
    {
        using var image = new Bitmap(301, 161);
        using (var graphics = Graphics.FromImage(image))
        {
            graphics.Clear(Color.White);
            using var gridPen = new Pen(Color.FromArgb(180, 190, 198));
            foreach (var x in new[] { 60, 120, 180, 240, 299 })
            {
                graphics.DrawLine(gridPen, x, 0, x, 159);
            }
            foreach (var y in new[] { 40, 80, 120, 159 })
            {
                graphics.DrawLine(gridPen, 0, y, 299, y);
            }
        }

        var ocr = new OcrRecognitionResult(true, string.Empty, null)
        {
            Words =
            [
                new OcrWordRegion("项目", 12, 10, 30, 18),
                new OcrWordRegion("开始时间", 68, 10, 44, 18),
                new OcrWordRegion("场地", 12, 50, 30, 18),
                new OcrWordRegion("7月28日", 68, 50, 44, 18),
                new OcrWordRegion("设备", 12, 90, 30, 18),
                new OcrWordRegion("7月29日", 68, 90, 44, 18),
                new OcrWordRegion("人力", 12, 130, 30, 18),
                new OcrWordRegion("8月26日", 68, 130, 44, 18),
            ],
        };

        var result = TableRecognitionService.BuildTsv(ocr, image);

        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Equal(4, Regex.Matches(result.ClipboardHtml!, "<tr\\b").Count);
        Assert.Contains("项目", result.ClipboardHtml);
        Assert.Contains("场地", result.ClipboardHtml);
        Assert.StartsWith("项目\t开始时间", result.Content);
    }

    [Fact]
    public void GridTableUsesSupplementaryWordsForVerticalNumbersAndDates()
    {
        using var image = new Bitmap(246, 186);
        using (var graphics = Graphics.FromImage(image))
        {
            graphics.Clear(Color.White);
            using var gridPen = new Pen(Color.FromArgb(180, 190, 198));
            foreach (var x in new[] { 5, 65, 125, 185, 240 })
            {
                graphics.DrawLine(gridPen, x, 5, x, 180);
            }
            foreach (var y in new[] { 5, 65, 125, 180 })
            {
                graphics.DrawLine(gridPen, 5, y, 240, y);
            }
        }

        var primary = new OcrRecognitionResult(true, string.Empty, null)
        {
            Words =
            [
                new OcrWordRegion("I9", 78, 15, 20, 42),
                new OcrWordRegion("7", 18, 70, 12, 10),
                new OcrWordRegion("月", 18, 82, 12, 10),
                new OcrWordRegion("8", 18, 94, 12, 10),
                new OcrWordRegion("2", 18, 106, 12, 10),
                new OcrWordRegion("日", 18, 118, 12, 5),
                new OcrWordRegion("%9", 142, 78, 22, 20),
                new OcrWordRegion("项目", 12, 140, 30, 18),
                new OcrWordRegion("2", 202, 16, 10, 14),
                new OcrWordRegion("3", 202, 38, 10, 14),
            ],
        };
        OcrWordRegion[] supplementary =
        [
            new("51", 82, 10, 20, 50),
            new("28", 18, 88, 12, 24),
            new("5", 142, 78, 12, 18),
            new("2531", 188, 8, 48, 52),
        ];

        var result = TableRecognitionService.BuildTsv(
            primary,
            image,
            supplementary);

        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Contains("5<br>1", result.ClipboardHtml);
        Assert.Contains("2<br>3", result.ClipboardHtml);
        Assert.DoesNotContain("2<br>5<br>3<br>1", result.ClipboardHtml);
        Assert.Contains("7月28日", result.ClipboardHtml);
        Assert.Contains("5%", result.ClipboardHtml);
    }

    [Fact]
    public void GridTableKeepsNarrowVerticalTextTogetherWhenCopied()
    {
        using var image = new Bitmap(181, 241);
        using (var graphics = Graphics.FromImage(image))
        {
            graphics.Clear(Color.White);
            using var gridPen = new Pen(Color.FromArgb(180, 190, 198));
            foreach (var x in new[] { 5, 45, 105, 175 })
            {
                graphics.DrawLine(gridPen, x, 5, x, 235);
            }
            foreach (var y in new[] { 5, 55, 175, 235 })
            {
                graphics.DrawLine(gridPen, 5, y, 175, y);
            }
        }

        var primary = new OcrRecognitionResult(true, string.Empty, null)
        {
            Words =
            [
                new OcrWordRegion("项目", 10, 18, 28, 18),
                new OcrWordRegion("日期", 60, 18, 28, 18),
                new OcrWordRegion("状态", 120, 18, 28, 18),
                new OcrWordRegion("开", 18, 68, 14, 16),
                new OcrWordRegion("始", 18, 88, 14, 16),
                new OcrWordRegion("时", 18, 108, 14, 16),
                new OcrWordRegion("间", 18, 128, 14, 16),
                new OcrWordRegion("7", 68, 68, 10, 16),
                new OcrWordRegion("月", 68, 88, 14, 16),
                new OcrWordRegion("28", 64, 108, 22, 16),
                new OcrWordRegion("日", 68, 128, 14, 16),
                new OcrWordRegion("On-", 120, 68, 28, 16),
                new OcrWordRegion("go", 120, 88, 22, 16),
                new OcrWordRegion("in", 120, 108, 18, 16),
                new OcrWordRegion("g", 120, 128, 10, 16),
                new OcrWordRegion("场地", 10, 196, 28, 18),
                new OcrWordRegion("12月5日", 58, 196, 42, 18),
                new OcrWordRegion("完成", 120, 196, 28, 18),
            ],
        };

        var result = TableRecognitionService.BuildTsv(primary, image);

        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Contains(">开始时间</td>", result.ClipboardHtml);
        Assert.Contains(">7月28日</td>", result.ClipboardHtml);
        Assert.Contains(">On-going</td>", result.ClipboardHtml);
        Assert.DoesNotContain("开<br>始", result.ClipboardHtml);
        Assert.Contains("white-space:nowrap", result.ClipboardHtml);
    }

    [Fact]
    public void GridTableDoesNotTreatHeaderTextEdgeAsASeparator()
    {
        using var image = new Bitmap(186, 111);
        using (var graphics = Graphics.FromImage(image))
        {
            graphics.Clear(Color.White);
            using var gridPen = new Pen(Color.FromArgb(180, 190, 198));
            using var textPen = new Pen(Color.FromArgb(20, 30, 40));
            graphics.DrawRectangle(gridPen, 5, 5, 175, 100);
            graphics.DrawLine(gridPen, 5, 55, 180, 55);
            graphics.DrawLine(gridPen, 65, 55, 65, 105);
            graphics.DrawLine(gridPen, 125, 55, 125, 105);
            graphics.DrawLine(textPen, 125, 15, 125, 45);
        }

        var ocr = new OcrRecognitionResult(true, string.Empty, null)
        {
            Words =
            [
                new OcrWordRegion("8月9月", 88, 14, 38, 32),
                new OcrWordRegion("1", 28, 70, 10, 16),
                new OcrWordRegion("2", 88, 70, 10, 16),
                new OcrWordRegion("3", 148, 70, 10, 16),
            ],
        };

        var result = TableRecognitionService.BuildTsv(ocr, image);

        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Contains("colspan=\"3\"", result.ClipboardHtml);
        Assert.DoesNotContain("8月9月</td><td", result.ClipboardHtml);
    }

    [Fact]
    public void GridTableRecoversShortDetailSeparatorsInARegularTimeline()
    {
        using var image = new Bitmap(601, 901);
        using (var graphics = Graphics.FromImage(image))
        {
            graphics.Clear(Color.White);
            using var gridPen = new Pen(Color.FromArgb(180, 190, 198));
            foreach (var y in new[] { 5, 65, 135, 300, 500, 895 })
            {
                graphics.DrawLine(gridPen, 5, y, 595, y);
            }
            foreach (var x in new[] { 5, 55, 105, 155, 205, 245, 415, 585, 595 })
            {
                graphics.DrawLine(gridPen, x, 5, x, 895);
            }
            foreach (var x in new[] { 279, 313, 347, 381, 483, 551 })
            {
                graphics.DrawLine(gridPen, x, 65, x, 300);
            }

            // These separators only exist in the shallow detail row. Their
            // global edge score is intentionally too low for the first pass.
            graphics.DrawLine(gridPen, 449, 65, 449, 135);
            graphics.DrawLine(gridPen, 517, 65, 517, 135);
        }

        var lines = TableRecognitionService.FindGridLinePositions(image, []);

        Assert.Contains(lines.Vertical, position => Math.Abs(position - 449) <= 2);
        Assert.Contains(lines.Vertical, position => Math.Abs(position - 517) <= 2);
    }

    [Fact]
    public void GridTableNormalizesTimelineNoiseAndPercentageColumn()
    {
        using var image = new Bitmap(211, 161);
        using (var graphics = Graphics.FromImage(image))
        {
            graphics.Clear(Color.White);
            using var gridPen = new Pen(Color.FromArgb(180, 190, 198));
            graphics.DrawRectangle(gridPen, 5, 5, 200, 150);
            foreach (var y in new[] { 55, 105 })
            {
                graphics.DrawLine(gridPen, 5, y, 205, y);
            }
            graphics.DrawLine(gridPen, 55, 5, 55, 155);
            graphics.DrawLine(gridPen, 105, 55, 105, 155);
            graphics.DrawLine(gridPen, 155, 55, 155, 155);
        }

        var primary = new OcrRecognitionResult(true, string.Empty, null)
        {
            Words =
            [
                new OcrWordRegion("完成比例", 12, 14, 32, 30),
                new OcrWordRegion("8月9月", 92, 14, 48, 26),
                new OcrWordRegion("てしヤ", 67, 67, 22, 28),
                new OcrWordRegion("2", 122, 72, 10, 16),
                new OcrWordRegion("3", 172, 72, 10, 16),
                new OcrWordRegion("31", 18, 122, 18, 16),
                new OcrWordRegion("任务", 82, 122, 30, 16),
            ],
        };
        OcrWordRegion[] supplementary =
        [
            new("51", 68, 62, 20, 36),
        ];

        var result = TableRecognitionService.BuildTsv(
            primary,
            image,
            supplementary);

        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Contains("5<br>1", result.ClipboardHtml);
        Assert.Contains("31%", result.ClipboardHtml);
        Assert.DoesNotContain("て", result.ClipboardHtml);
        Assert.DoesNotContain("し", result.ClipboardHtml);
        Assert.DoesNotContain("ヤ", result.ClipboardHtml);
    }

    [Fact]
    public void TranslationPresentationGroupsWrappedLinesIntoParagraphs()
    {
        var grouped = TranslationPresentationLayout.GroupParagraphs(
        [
            new OcrTextRegion("The first wrapped line", 10, 10, 280, 20),
            new OcrTextRegion("continues on the next line.", 11, 34, 250, 20),
            new OcrTextRegion("A heading", 10, 78, 120, 30),
        ]);

        Assert.Equal(2, grouped.Count);
        Assert.Equal(
            "The first wrapped line continues on the next line.",
            grouped[0].Text);
        Assert.Equal(10, grouped[0].X);
        Assert.Equal(44, grouped[0].Height);
        Assert.Equal(20 / 1.12, grouped[0].EstimatedFontSize, precision: 6);
        Assert.Equal("A heading", grouped[1].Text);
        Assert.Equal(30 / 1.12, grouped[1].EstimatedFontSize, precision: 6);
    }

    [Fact]
    public void TranslationPresentationGroupsLinesWithOcrVerticalOverlap()
    {
        var grouped = TranslationPresentationLayout.GroupParagraphs(
        [
            new OcrTextRegion("第一行", 10, 10, 220, 24),
            new OcrTextRegion("第二行", 12, 22, 210, 18),
        ]);

        var paragraph = Assert.Single(grouped);
        Assert.Equal("第一行 第二行", paragraph.Text);
        Assert.Equal(30, paragraph.Height);
    }

    [Theory]
    [InlineData("住宅 I P 地址", "住宅 IP 地址")]
    [InlineData("应 当谨慎使用", "应当谨慎使用")]
    public void TranslationPresentationRemovesOcrSpacingArtifacts(
        string input,
        string expected)
    {
        Assert.Equal(
            expected,
            TranslationPresentationLayout.NormalizeTranslatedText(input));
    }

    [Theory]
    [InlineData("Connect", "连接", true)]
    [InlineData("Channel", "频道", true)]
    [InlineData("类别", "类别", false)]
    [InlineData("IDC Flare", "IDC Flare", false)]
    [InlineData("8", "八", false)]
    [InlineData("GI", "地理标志", false)]
    [InlineData("旦", "65E5", false)]
    [InlineData("  开发调优  ", "开发调优", false)]
    [InlineData("开 发 调 优", "开发调优", false)]
    public void TranslationOverlayOnlyReplacesActuallyTranslatedLines(
        string source,
        string translated,
        bool expected)
    {
        Assert.Equal(
            expected,
            TranslationPresentationLayout.HasMeaningfulTranslation(
                source,
                translated));
    }

    [Theory]
    [InlineData("Connect", "@ 连接", "连接")]
    [InlineData("More...", "8 更多...", "更多...")]
    [InlineData("dailyExercise", "· 每日练习", "每日练习")]
    [InlineData("Version 8", "版本 8", "版本 8")]
    public void TranslationPresentationRemovesUnexpectedLeadingArtifacts(
        string source,
        string translated,
        string expected)
    {
        Assert.Equal(
            expected,
            TranslationPresentationLayout.NormalizeTranslatedText(
                source,
                translated));
    }

    [Fact]
    public void TranslationPresentationUsesWordBoundsInsteadOfNearbyIcons()
    {
        var tightened = TranslationPresentationLayout.TightenToWordBounds(
        [
            new OcrTextRegion("cattiLearning", 8, 19, 118, 22),
            new OcrTextRegion("More...", 209, 195, 93, 31),
        ],
        [
            new OcrWordRegion("cattiLearning", 40, 19, 86, 22),
            new OcrWordRegion("8", 174, 199, 31, 23),
            new OcrWordRegion("More...", 226, 195, 76, 31),
        ]);

        Assert.Equal(new OcrTextRegion("cattiLearning", 40, 19, 86, 22), tightened[0]);
        Assert.Equal(new OcrTextRegion("More...", 226, 195, 76, 31), tightened[1]);
    }

    [Fact]
    public void TranslationPresentationSeparatesIconTextMergedIntoTheSameOcrLine()
    {
        var tightened = TranslationPresentationLayout.TightenToWordBounds(
        [
            new OcrTextRegion("8 More...", 174, 195, 128, 31),
            new OcrTextRegion("旦 文档共建", 181, 534, 131, 34),
        ],
        [
            new OcrWordRegion("8", 174, 199, 31, 23),
            new OcrWordRegion("More...", 226, 195, 76, 31),
            new OcrWordRegion("旦", 181, 537, 20, 29),
            new OcrWordRegion("文", 220, 534, 24, 34),
            new OcrWordRegion("档", 244, 534, 22, 34),
            new OcrWordRegion("共", 266, 534, 22, 34),
            new OcrWordRegion("建", 288, 534, 24, 34),
        ]);

        Assert.Equal(new OcrTextRegion("More...", 226, 195, 76, 31), tightened[0]);
        Assert.Equal(new OcrTextRegion("文档共建", 220, 534, 92, 34), tightened[1]);
    }

    [Fact]
    public void SelectableOcrWordSupportsContinuousCharactersAndReverseSelection()
    {
        WpfTestHost.Invoke(() =>
        {
            var overlay = new SelectableOcrTextOverlay(
                [new OcrWordRegion("快捷键", 10, 8, 60, 20)],
                scaleX: 1,
                scaleY: 1,
                System.Windows.Media.Colors.Teal);

            overlay.SelectTextRange(0, 0, 0, 3);
            Assert.Equal("快捷键", overlay.SelectedText);

            overlay.SelectTextRange(0, 3, 0, 0);
            Assert.Equal("快捷键", overlay.SelectedText);
        });
    }

    [Fact]
    public void SelectableOcrWordsKeepVisualLineOrderAndJoinCjkWithoutSpaces()
    {
        WpfTestHost.Invoke(() =>
        {
            var overlay = new SelectableOcrTextOverlay(
            [
                new OcrWordRegion("捷", 36, 8.5, 18, 20),
                new OcrWordRegion("下一行", 10, 36, 54, 20),
                new OcrWordRegion("快", 10, 9, 18, 20),
                new OcrWordRegion("键", 62, 8, 18, 20),
            ],
            scaleX: 1,
            scaleY: 1,
            System.Windows.Media.Colors.Teal);

            overlay.SelectAllText();

            Assert.Equal(
                $"快捷键{Environment.NewLine}下一行",
                overlay.SelectedText);
        });
    }

    [Fact]
    public void SelectableOcrWordsKeepSpacesBetweenLatinWords()
    {
        WpfTestHost.Invoke(() =>
        {
            var overlay = new SelectableOcrTextOverlay(
            [
                new OcrWordRegion("global", 52, 8, 44, 20),
                new OcrWordRegion("Hello", 10, 9, 36, 20),
            ],
            scaleX: 1,
            scaleY: 1,
            System.Windows.Media.Colors.Teal);

            overlay.SelectAllText();

            Assert.Equal("Hello global", overlay.SelectedText);
        });
    }

    [Fact]
    public void TranslationLayoutUsesTheSameExplicitLinesForDisplayAndSelection()
    {
        WpfTestHost.Invoke(() =>
        {
            var region = new TranslatedTextAnnotationRegion(
                new System.Windows.Rect(10, 20, 300, 150),
                "这是一段需要自动换行并保持选择位置一致的中文翻译文本。",
                32);

            var layout = TranslationTextLayout.LayoutParagraph(region);

            Assert.NotEmpty(layout.Lines);
            Assert.All(layout.Lines, line =>
            {
                Assert.InRange(line.X, region.Bounds.Left, region.Bounds.Right);
                Assert.InRange(line.Y, region.Bounds.Top, region.Bounds.Bottom);
                Assert.True(line.Width <= region.Bounds.Width);
                Assert.True(line.Height > 0);
            });
        });
    }

    [Fact]
    public async Task FormulaRecognitionSendsImageToConfiguredVisionModel()
    {
        using var image = new CapturedImage(new Bitmap(24, 16));
        var handler = new RecordingHandler();
        using var httpClient = new HttpClient(handler);
        var result = await FormulaRecognitionService.RecognizeAsync(
            image,
            new AppSettings
            {
                TranslationProvider = "OpenAI",
                TranslationEndpoint = "https://example.com/v1",
                TranslationModel = "vision-model",
            },
            new TestCredentialStore("test-key"),
            httpClient);

        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Equal("x^2 + y^2 = z^2", result.Content);
        Assert.Contains("data:image/png;base64,", handler.RequestBody);
        Assert.Contains("vision-model", handler.RequestBody);
    }

    private sealed class TestCredentialStore(string apiKey)
        : ITranslationCredentialStore
    {
        public string? GetApiKey(string providerId) => apiKey;

        public void SetApiKey(string providerId, string? value)
        {
        }
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public string RequestBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"choices\":[{\"message\":{\"content\":\"x^2 + y^2 = z^2\"}}]}",
                    Encoding.UTF8,
                    "application/json"),
            };
        }
    }
}
