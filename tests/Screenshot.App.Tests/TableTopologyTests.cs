using System.Drawing;
using System.Text.RegularExpressions;
using Screenshot.App.Text;

namespace Screenshot.App.Tests;

public sealed class TableTopologyTests
{
    [Fact]
    public void CaptionOutsideTheRulesDoesNotCreateAnExtraTableRow()
    {
        using var bitmap = new Bitmap(250, 170);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.White);
            foreach (var x in new[] { 10, 120, 240 }) graphics.DrawLine(Pens.Gray, x, 40, x, 140);
            foreach (var y in new[] { 40, 90, 140 }) graphics.DrawLine(Pens.Gray, 10, y, 240, y);
        }
        var ocr = new OcrRecognitionResult(true, "", null) { Words = [
            new("表格标题", 25, 5, 80, 20), new("编号", 25, 55, 40, 20), new("名称", 140, 55, 40, 20),
            new("01", 25, 105, 24, 20), new("设备", 140, 105, 40, 20)] };
        var result = TableRecognitionService.BuildTsv(ocr, bitmap);
        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Equal("编号\t名称" + Environment.NewLine + "01\t设备", result.Content);
    }

    [Fact]
    public void ShortHeaderSeparatorSurvivesATallTable()
    {
        using var bitmap = new Bitmap(501, 1661);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.White);
            foreach (var x in new[] { 10, 170, 330, 490 }) graphics.DrawLine(Pens.Gray, x, 10, x, 1660);
            for (var row = 0; row <= 55; row++) graphics.DrawLine(Pens.Gray, 10, 10 + row * 30, 490, 10 + row * 30);
            graphics.DrawLine(Pens.Gray, 90, 10, 90, 40);
        }
        var words = new List<OcrWordRegion> { new("A1", 25, 16, 24, 18), new("A2", 105, 16, 24, 18), new("B", 230, 16, 16, 18), new("C", 390, 16, 16, 18) };
        for (var row = 1; row < 55; row++)
        for (var col = 0; col < 3; col++) words.Add(new($"{row}-{col}", 25 + col * 160, 16 + row * 30, 50, 18));
        var table = TableRecognitionService.BuildTsv(new(true, "", null) { Words = words }, bitmap);
        Assert.True(table.IsSuccess, table.ErrorMessage);
        Assert.StartsWith("A1\tA2\tB\tC", table.Content);
        Assert.Equal(55, table.Content.Split(Environment.NewLine).Length);
        Assert.Equal(54, Regex.Matches(table.ClipboardHtml!, "colspan=\"2\"").Count);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void OpenSidesPreserveBothOuterColumnsAndMergedHeaders(bool leftBorder, bool rightBorder)
    {
        using var bitmap = new Bitmap(460, 240);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.White);
            foreach (var y in new[] { 20, 65, 110, 155, 200 }) graphics.DrawLine(Pens.Gray, 20, y, 420, y);
            if (leftBorder) graphics.DrawLine(Pens.Gray, 20, 20, 20, 200);
            if (rightBorder) graphics.DrawLine(Pens.Gray, 420, 20, 420, 200);
            graphics.DrawLine(Pens.Gray, 120, 65, 120, 200);
            graphics.DrawLine(Pens.Gray, 220, 20, 220, 200);
            graphics.DrawLine(Pens.Gray, 320, 65, 320, 200);
        }
        var words = new List<OcrWordRegion> { new("左组合", 75, 35, 55, 18), new("右组合", 275, 35, 55, 18) };
        for (var row = 0; row < 3; row++)
        for (var col = 0; col < 4; col++) words.Add(new($"R{row}C{col}", 35 + col * 100, 80 + row * 45, 52, 18));
        var result = TableRecognitionService.BuildTsv(new(true, "", null) { Words = words }, bitmap);
        Assert.True(result.IsSuccess, result.ErrorMessage);
        var rows = result.Content.Split(Environment.NewLine);
        Assert.Equal(4, rows.Length);
        Assert.All(rows, row => Assert.Equal(4, row.Split('\t').Length));
        Assert.Equal("R2C0\tR2C1\tR2C2\tR2C3", rows[3]);
        Assert.Equal(2, Regex.Matches(result.ClipboardHtml!, "colspan=\"2\"").Count);
    }

    [Fact]
    public void ThreeLineTableInfersColumnsWithoutVerticalRules()
    {
        using var bitmap = new Bitmap(410, 210);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.White);
            foreach (var y in new[] { 10, 60, 110, 160 }) graphics.DrawLine(Pens.Gray, 10, y, 390, y);
        }
        var words = new List<OcrWordRegion>();
        for (var row = 0; row < 3; row++)
        for (var col = 0; col < 3; col++) words.Add(new($"{row}{col}", 30 + col * 130, 27 + row * 50, 28, 18));
        var result = TableRecognitionService.BuildTsv(new(true, "", null) { Words = words }, bitmap);
        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Equal("00\t01\t02" + Environment.NewLine + "10\t11\t12" + Environment.NewLine + "20\t21\t22", result.Content);
    }

    [Fact]
    public void ThreeRulesKeepMultipleBodyRowsSeparate()
    {
        using var bitmap = new Bitmap(410, 270);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.White);
            foreach (var y in new[] { 10, 65, 250 }) graphics.DrawLine(Pens.Gray, 10, y, 390, y);
        }
        var words = new List<OcrWordRegion>();
        for (var row = 0; row < 4; row++)
        for (var col = 0; col < 3; col++) words.Add(new($"{row}{col}", 30 + col * 130, 27 + row * 55, 28, 18));
        var result = TableRecognitionService.BuildTsv(new(true, "", null) { Words = words }, bitmap);
        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Equal(4, result.Content.Split(Environment.NewLine).Length);
        Assert.EndsWith("30\t31\t32", result.Content);
    }

    [Fact]
    public void NestedHeaderAndRowMergesKeepEveryCellInPlace()
    {
        using var bitmap = new Bitmap(410, 270);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.White);
            foreach (var y in new[] { 10, 110, 250 }) graphics.DrawLine(Pens.Gray, 10, y, 390, y);
            graphics.DrawLine(Pens.Gray, 110, 60, 390, 60);
            graphics.DrawLine(Pens.Gray, 110, 180, 390, 180);
            graphics.DrawLine(Pens.Gray, 110, 10, 110, 250);
            graphics.DrawLine(Pens.Gray, 250, 10, 250, 250);
            graphics.DrawLine(Pens.Gray, 180, 60, 180, 250);
            graphics.DrawLine(Pens.Gray, 320, 60, 320, 250);
        }
        var words = new List<OcrWordRegion> { new("项目", 30, 42, 50, 18), new("A组", 155, 25, 40, 18), new("B组", 295, 25, 40, 18), new("合并行", 25, 152, 60, 18) };
        for (var row = 0; row < 3; row++)
        for (var col = 0; col < 4; col++) words.Add(new($"{row}{col}", 123 + col * 70, 75 + row * 65, 28, 18));
        var result = TableRecognitionService.BuildTsv(new(true, "", null) { Words = words }, bitmap);
        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Equal(4, result.Content.Split(Environment.NewLine).Length);
        Assert.Equal(2, Regex.Matches(result.ClipboardHtml!, "rowspan=\"2\"").Count);
        Assert.Equal(2, Regex.Matches(result.ClipboardHtml!, "colspan=\"2\"").Count);
        Assert.Equal("\t20\t21\t22\t23", result.Content.Split(Environment.NewLine)[3]);
    }

    [Fact]
    public void MergingCannotTravelAroundAnIntactInteriorBorder()
    {
        var vertical = new bool[2, 3];
        var horizontal = new bool[3, 2];
        vertical[0, 1] = true;
        var cells = TableRecognitionService.PartitionCells(2, 2, vertical, horizontal);
        Assert.DoesNotContain(cells, cell => cell.Contains(0) && cell.Contains(1));
        Assert.All(cells, cell => Assert.Equal(cell.Length,
            (cell.Max(x => x / 2) - cell.Min(x => x / 2) + 1) * (cell.Max(x => x % 2) - cell.Min(x => x % 2) + 1)));
        Assert.Equal(Enumerable.Range(0, 4), cells.SelectMany(cell => cell).Order());
    }

    [Fact]
    public void SupplementaryTextFillsAnEmptyChineseCell()
    {
        using var bitmap = new Bitmap(250, 130);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.White);
            foreach (var x in new[] { 10, 120, 240 }) graphics.DrawLine(Pens.Gray, x, 10, x, 120);
            foreach (var y in new[] { 10, 65, 120 }) graphics.DrawLine(Pens.Gray, 10, y, 240, y);
        }
        var primary = new OcrRecognitionResult(true, "", null) { Words = [new("编号", 25, 25, 40, 20), new("名称", 140, 25, 40, 20), new("01", 25, 80, 24, 20)] };
        var result = TableRecognitionService.BuildTsv(primary, bitmap, [new("设备检查", 140, 80, 75, 20)]);
        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Contains("01\t设备检查", result.Content);
    }
}
