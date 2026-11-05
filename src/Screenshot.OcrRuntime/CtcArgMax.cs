// Apache-2.0. Windows optimization of RapidOcrNet's CTC score scan.
using System.Numerics;

namespace RapidOcrNet;

internal static class CtcArgMax
{
    internal static (int Index, float Score) Find(ReadOnlySpan<float> scores)
    {
        var maximum = -1000f;
        var offset = 0;
        if (Vector.IsHardwareAccelerated && scores.Length >= Vector<float>.Count)
        {
            var maxima = new Vector<float>(maximum);
            for (; offset <= scores.Length - Vector<float>.Count; offset += Vector<float>.Count)
            {
                var values = new Vector<float>(scores.Slice(offset, Vector<float>.Count));
                // Match the scalar comparison: NaNs never replace a maximum.
                maxima = Vector.ConditionalSelect(Vector.GreaterThan(values, maxima), values, maxima);
            }
            for (var lane = 0; lane < Vector<float>.Count; lane++)
                if (maxima[lane] > maximum) maximum = maxima[lane];
        }
        for (; offset < scores.Length; offset++)
            if (scores[offset] > maximum) maximum = scores[offset];
        // CTC requires the FIRST maximum, including ties across SIMD lanes.
        return (maximum > -1000f ? scores.IndexOf(maximum) : 0, maximum);
    }
}
