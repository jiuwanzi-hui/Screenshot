using RapidOcrNet;

namespace Screenshot.App.Tests;

public sealed class OcrDecoderTests
{
    [Fact]
    public void VectorScoreScanMatchesScalarIncludingTiesNanAndTail()
    {
        var random = new Random(1940);
        foreach (var count in new[] { 0, 1, 7, 8, 9, 15, 32, 128, 18386 })
        {
            for (var iteration = 0; iteration < 12; iteration++)
            {
                var values = Enumerable.Range(0, count).Select(_ => (float)(random.NextDouble() - 0.5)).ToArray();
                if (count > 7)
                {
                    values[1] = float.NaN;
                    values[3] = values[^1] = iteration % 2 == 0 ? 1f : float.PositiveInfinity;
                }
                Assert.Equal(Scalar(values), CtcArgMax.Find(values));
            }
        }
        Assert.Equal((0, -1000f), CtcArgMax.Find(new[] { float.NaN, -2000f, float.NegativeInfinity }));
    }

    private static (int Index, float Score) Scalar(float[] values)
    {
        var index = 0;
        var maximum = -1000f;
        for (var i = 0; i < values.Length; i++)
            if (values[i] > maximum) { index = i; maximum = values[i]; }
        return (index, maximum);
    }
}
