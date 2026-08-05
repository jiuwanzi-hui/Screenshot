using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Net;
using System.Net.Http;
using System.Text;
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
