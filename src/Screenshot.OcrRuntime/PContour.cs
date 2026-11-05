// MIT License
//
// Copyright (c) 2021 Lingdong Huang
// Copyright (c) 2024 BobLd
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all
// copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
// SOFTWARE.

// Ported from https://github.com/LingDong-/PContour/commit/64b943072491bcfd5e9e73676981f53f54cf2c75

// See https://github.com/BobLd/PContourNet

using System.Runtime.InteropServices;
using SkiaSharp;

namespace RapidOcrNet
{
    internal static class PContour
    {
        private const int N_PIXEL_NEIGHBOR = 8;

        /// <summary>
        /// Give pixel neighborhood counter-clockwise ID's for easier access with findContour algorithm.
        /// </summary>
        private static void NeighborIdToIndex(int i, int j, int id, out int x, out int y)
        {
            switch (id)
            {
                case 0:
                    x = i;
                    y = j + 1;
                    return;
                case 1:
                    x = i - 1;
                    y = j + 1;
                    return;
                case 2:
                    x = i - 1;
                    y = j;
                    return;
                case 3:
                    x = i - 1;
                    y = j - 1;
                    return;
                case 4:
                    x = i;
                    y = j - 1;
                    return;
                case 5:
                    x = i + 1;
                    y = j - 1;
                    return;
                case 6:
                    x = i + 1;
                    y = j;
                    return;
                case 7:
                    x = i + 1;
                    y = j + 1;
                    return;
                default:
                    throw new ArgumentOutOfRangeException($"Could not find position for id '{id}'. Value should be between 0 and 7 inclusive.", nameof(id));
            }
        }

        private static int NeighborIndexToId(int i0, int j0, int i, int j)
        {
            int di = i - i0;
            int dj = j - j0;
            if (di == 0 && dj == 1)
            {
                return 0;
            }

            if (di == -1 && dj == 1)
            {
                return 1;
            }

            if (di == -1 && dj == 0)
            {
                return 2;
            }

            if (di == -1 && dj == -1)
            {
                return 3;
            }

            if (di == 0 && dj == -1)
            {
                return 4;
            }

            if (di == 1 && dj == -1)
            {
                return 5;
            }

            if (di == 1 && dj == 0)
            {
                return 6;
            }

            if (di == 1 && dj == 1)
            {
                return 7;
            }

            return -1;
        }

        /// <summary>
        /// First counter clockwise non-zero element in neighborhood.
        /// </summary>
        private static bool ccwNon0(ReadOnlySpan<int> F, int w, int h, int i0, int j0, int i, int j, int offset, out int x, out int y)
        {
            int id = NeighborIndexToId(i0, j0, i, j);
            for (int k = 0; k < N_PIXEL_NEIGHBOR; k++)
            {
                int kk = (k + id + offset + N_PIXEL_NEIGHBOR * 2) % N_PIXEL_NEIGHBOR;

                NeighborIdToIndex(i0, j0, kk, out int i1, out int j1);
                if (F[i1 * w + j1] != 0)
                {
                    x = i1;
                    y = j1;
                    return true;
                }
            }

            x = -1;
            y = -1;
            return false;
        }

        /// <summary>
        /// First clockwise non-zero element in neighborhood.
        /// </summary>
        private static bool cwNon0(ReadOnlySpan<int> F, int w, int h, int i0, int j0, int i, int j, int offset, out int x, out int y)
        {
            int id = NeighborIndexToId(i0, j0, i, j);
            for (int k = 0; k < N_PIXEL_NEIGHBOR; k++)
            {
                int kk = (-k + id - offset + N_PIXEL_NEIGHBOR * 2) % N_PIXEL_NEIGHBOR;
                NeighborIdToIndex(i0, j0, kk, out int i1, out int j1);
                if (F[i1 * w + j1] != 0)
                {
                    x = i1;
                    y = j1;
                    return true;
                }
            }

            x = -1;
            y = -1;
            return false;
        }

        /// <summary>
        /// Data structure for a contour, encodes vertices as well as hierarchical relationship to other contours.
        /// </summary>
        public sealed class Contour
        {
            /// <summary>
            /// Vertices.
            /// </summary>
            internal List<SKPoint> points;

            /// <summary>
            /// Unique ID, starts from 2.
            /// </summary>
            public int id;

            /// <summary>
            /// ID of parent contour, 0 means top-level contour.
            /// </summary>
            public int parent;

            /// <summary>
            /// Is this contour a hole (as opposed to outline).
            /// </summary>
            public bool isHole;

            /// <summary>
            /// Vertices.
            /// </summary>
            public Span<SKPoint> GetSpan()
            {
                ArgumentNullException.ThrowIfNull(points, nameof(points));

                return CollectionsMarshal.AsSpan(points);
            }
        }

        /// <summary>
        /// Find contours in a binary image.
        /// <para>
        /// Implements Suzuki, S. and Abe, K.
        /// Topological Structural Analysis of Digitized Binary Images by Border Following.
        /// </para>
        /// See source code for step-by-step correspondence to the paper's algorithm description.
        /// </summary>
        /// <param name="F">The bitmap, stored in 1-dimensional row-major form.
        /// 0=background,
        /// 1=foreground, will be modified by the function to hold semantic information.</param>
        /// <param name="w">Width of the bitmap.</param>
        /// <param name="h">Height of the bitmap.</param>
        /// <returns>An array of contours found in the image.</returns>
        public static IReadOnlyList<Contour> FindContours(Span<int> F, int w, int h)
        {
            // Topological Structural Analysis of Digitized Binary Images by Border Following.
            // Suzuki, S. and Abe, K., CVGIP 30 1, pp 32-46 (1985)
            int nbd = 1;
            int lnbd = 1;

            List<Contour> contours = new List<Contour>();

            // Without loss of generality, we assume that 0-pixels fill the frame
            // of a binary picture
            for (int i = 1; i < h - 1; i++)
            {
                F[i * w] = 0;
                F[i * w + w - 1] = 0;
            }

            for (int i = 0; i < w; i++)
            {
                F[i] = 0;
                F[w * h - 1 - i] = 0;
            }

            // Scan the picture with a TV raster and perform the following steps
            // for each pixel such that fij # 0. Every time we begin to scan a
            // new row of the picture, reset LNBD to 1.
            for (int i = 1; i < h - 1; i++)
            {
                lnbd = 1;

                for (int j = 1; j < w - 1; j++)
                {

                    int i2 = 0, j2 = 0;
                    if (F[i * w + j] == 0)
                    {
                        continue;
                    }

                    // (a) If fij = 1 and fi, j-1 = 0, then decide that the pixel
                    // (i, j) is the border following starting point of an outer
                    // border, increment NBD, and (i2, j2) <- (i, j - 1).
                    if (F[i * w + j] == 1 && F[i * w + (j - 1)] == 0)
                    {
                        nbd++;
                        i2 = i;
                        j2 = j - 1;

                        // (b) Else if fij >= 1 and fi,j+1 = 0, then decide that the
                        // pixel (i, j) is the border following starting point of a
                        // hole border, increment NBD, (i2, j2) <- (i, j + 1), and
                        // LNBD + fij in case fij > 1.
                    }
                    else if (F[i * w + j] >= 1 && F[i * w + j + 1] == 0)
                    {
                        nbd++;
                        i2 = i;
                        j2 = j + 1;
                        if (F[i * w + j] > 1)
                        {
                            lnbd = F[i * w + j];
                        }
                    }
                    else
                    {
                        // (c) Otherwise, go to (4).
                        // (4) If fij != 1, then LNBD <- |fij| and resume the raster
                        // scan from pixel (i,j+1). The algorithm terminates when the
                        // scan reaches the lower right corner of the picture
                        if (F[i * w + j] != 1)
                        {
                            lnbd = Math.Abs(F[i * w + j]);
                        }

                        continue;
                    }

                    // (2) Depending on the types of the newly found border
                    // and the border with the sequential number LNBD
                    // (i.e., the last border met on the current row),
                    // decide the parent of the current border as shown in Table 1.
                    // TABLE 1
                    // Decision Rule for the Parent Border of the Newly Found Border B
                    // ----------------------------------------------------------------
                    // Type of border B'
                    // \    with the sequential
                    //     \     number LNBD
                    // Type of B \                Outer border         Hole border
                    // ---------------------------------------------------------------
                    // Outer border               The parent border    The border B'
                    //                            of the border B'
                    //
                    // Hole border                The border B'      The parent border
                    //                                               of the border B'
                    // ----------------------------------------------------------------

                    var B = new Contour
                    {
                        isHole = j2 == j + 1,
                        id = nbd,
                        points =
                        [
                            new SKPoint(j, i)
                        ]
                    };

                    contours.Add(B);

                    var B0 = new Contour();
                    foreach (var c in contours)
                    {
                        if (c.id == lnbd)
                        {
                            B0 = c;
                            break;
                        }
                    }

                    if (B0.isHole)
                    {
                        B.parent = B.isHole ? B0.parent : lnbd;
                    }
                    else
                    {
                        B.parent = B.isHole ? lnbd : B0.parent;
                    }

                    // (3) From the starting point (i, j), follow the detected border:
                    // this is done by the following substeps (3.1) through (3.5).

                    // (3.1) Starting from (i2, j2), look around clockwise the pixels
                    // in the neighborhood of (i, j) and tind a nonzero pixel.
                    // Let (i1, j1) be the first found nonzero pixel. If no nonzero
                    // pixel is found, assign -NBD to fij and go to (4).

                    if (!cwNon0(F, w, h, i, j, i2, j2, 0, out int i1, out int j1))
                    {
                        F[i * w + j] = -nbd;
                        // go to (4)
                        if (F[i * w + j] != 1)
                        {
                            lnbd = Math.Abs(F[i * w + j]);
                        }

                        continue;
                    }

                    // (3.2) (i2, j2) <- (i1, j1) ad (i3,j3) <- (i, j).
                    i2 = i1;
                    j2 = j1;
                    int i3 = i;
                    int j3 = j;

                    while (true)
                    {
                        // (3.3) Starting from the next element of the pixel (i2, j2)
                        // in the counterclockwise order, examine counterclockwise
                        // the pixels in the neighborhood of the current pixel (i3, j3)
                        // to find a nonzero pixel and let the first one be (i4, j4).

                        if (!ccwNon0(F, w, h, i3, j3, i2, j2, 1, out int i4, out int j4))
                        {
                            throw new Exception("Could not find first counter clockwise non-zero element in neighborhood.");
                        }

                        contours[contours.Count - 1].points.Add(new SKPoint(j4, i4));

                        // (a) If the pixel (i3, j3 + 1) is a O-pixel examined in the
                        // substep (3.3) then fi3, j3 <-  -NBD.
                        if (F[i3 * w + j3 + 1] == 0)
                        {
                            F[i3 * w + j3] = -nbd;

                            // (b) If the pixel (i3, j3 + 1) is not a O-pixel examined
                            // in the substep (3.3) and fi3,j3 = 1, then fi3,j3 <- NBD.
                        }
                        else if (F[i3 * w + j3] == 1)
                        {
                            F[i3 * w + j3] = nbd;
                        }
                        else
                        {
                            // (c) Otherwise, do not change fi3, j3.
                        }

                        // (3.5) If (i4, j4) = (i, j) and (i3, j3) = (i1, j1)
                        // (coming back to the starting point), then go to (4);
                        if (i4 == i && j4 == j && i3 == i1 && j3 == j1)
                        {
                            if (F[i * w + j] != 1)
                            {
                                lnbd = Math.Abs(F[i * w + j]);
                            }

                            break;

                            // otherwise, (i2, j2) + (i3, j3),(i3, j3) + (i4, j4),
                            // and go back to (3.3).
                        }
                        else
                        {
                            i2 = i3;
                            j2 = j3;
                            i3 = i4;
                            j3 = j4;
                        }
                    }
                }
            }

            return contours;
        }

        private static float PointDistanceToSegment(SKPoint p, SKPoint p0, SKPoint p1)
        {
            // https://stackoverflow.com/a/6853926
            float x = p.X;
            float y = p.Y;
            float x1 = p0.X;
            float y1 = p0.Y;
            float x2 = p1.X;
            float y2 = p1.Y;
            float A = x - x1;
            float B = y - y1;
            float C = x2 - x1;
            float D = y2 - y1;
            float dot = A * C + B * D;
            float len_sq = C * C + D * D;
            float param = -1;
            if (len_sq != 0)
            {
                param = dot / len_sq;
            }

            float xx;
            float yy;
            if (param < 0)
            {
                xx = x1;
                yy = y1;
            }
            else if (param > 1)
            {
                xx = x2;
                yy = y2;
            }
            else
            {
                xx = x1 + param * C;
                yy = y1 + param * D;
            }

            float dx = x - xx;
            float dy = y - yy;
            return MathF.Sqrt(dx * dx + dy * dy);
        }

        /// <summary>
        /// Simplify contour by removing definitely extraneous vertices, without modifying shape of the contour.
        /// </summary>
        /// <param name="polyline">The vertices.</param>
        /// <returns>A simplified copy.</returns>
        public static ReadOnlySpan<SKPoint> ApproxPolySimple(ReadOnlySpan<SKPoint> polyline)
        {
            float epsilon = 0.1f;
            if (polyline.Length <= 2)
            {
                return polyline;
            }

            int p = 0;
            Span<SKPoint> ret = new SKPoint[polyline.Length];
            ret[p++] = polyline[0];

            for (int i = 1; i < polyline.Length - 1; i++)
            {
                float d = PointDistanceToSegment(polyline[i], polyline[i - 1], polyline[i + 1]);
                if (d > epsilon)
                {
                    ret[p++] = polyline[i];
                }
            }

            ret[p++] = polyline[polyline.Length - 1];
            return ret.Slice(0, p);
        }

        /// <summary>
        /// Simplify contour using Douglas Peucker algorithm.
        /// <para>
        /// Implements David Douglas and Thomas Peucker, "Algorithms for the reduction of the number of points required to
        /// represent a digitized line or its caricature",
        /// The Canadian Cartographer 10(2), 112–122 (1973)
        /// </para>
        /// </summary>
        /// <param name="polyline">The vertices.</param>
        /// <param name="epsilon">Maximum allowed error.</param>
        /// <returns>A simplified copy.</returns>
        public static ReadOnlySpan<SKPoint> ApproxPolyDP(ReadOnlySpan<SKPoint> polyline, float epsilon)
        {
            // https://en.wikipedia.org/wiki/Ramer–Douglas–Peucker_algorithm
            // David Douglas & Thomas Peucker,
            // "Algorithms for the reduction of the number of points required to
            // represent a digitized line or its caricature",
            // The Canadian Cartographer 10(2), 112–122 (1973)

            if (polyline.Length <= 2)
            {
                return polyline;
            }

            var first = polyline[0];
            var last = polyline[polyline.Length - 1];

            float dmax = 0;
            int argmax = -1;
            for (int i = 1; i < polyline.Length - 1; i++)
            {
                float d = PointDistanceToSegment(polyline[i], first, last);
                if (d > dmax)
                {
                    dmax = d;
                    argmax = i;
                }
            }

            int p = 0;
            Span<SKPoint> ret = new SKPoint[polyline.Length];

            if (dmax > epsilon)
            {
                ReadOnlySpan<SKPoint> L = ApproxPolyDP(polyline.Slice(0, argmax + 1), epsilon);
                foreach (var l in L.Slice(0, L.Length - 1))
                {
                    ret[p++] = l;
                }

                ReadOnlySpan<SKPoint> R = ApproxPolyDP(polyline.Slice(argmax), epsilon);
                foreach (var r in R)
                {
                    ret[p++] = r;
                }
            }
            else
            {
                ret[p++] = first;
                ret[p++] = last;
            }

            return ret.Slice(0, p);
        }
    }
}
