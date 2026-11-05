using Screenshot.App.Text;

namespace Screenshot.App.Tests;

public sealed class OcrReadingOrderTests
{
    [Fact]
    public void BaselineJitterDoesNotReorderSpacedCharactersOrMergeDifferentLines()
    {
        OcrTextRegion[] regions = [new("B", 100, 9, 30, 30), new("A", 10, 12, 30, 30),
            new("D", 100, 60, 30, 30), new("C", 10, 62, 30, 30)];
        Assert.Equal("ABCD", string.Concat(OcrReadingOrder.Sort(regions).Select(r => r.Text)));
    }

    [Fact]
    public void VerticalColumnsAreNotGroupedAsHorizontalCharacters()
    {
        OcrTextRegion[] regions = [new("title", 100, 50, 90, 25), new("column", 10, 10, 30, 180),
            new("next", 100, 100, 90, 25)];
        Assert.Equal("column|title|next", string.Join('|', OcrReadingOrder.Sort(regions).Select(r => r.Text)));
    }
}
