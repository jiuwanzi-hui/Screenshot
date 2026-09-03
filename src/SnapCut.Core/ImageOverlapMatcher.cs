namespace SnapCut.Core;

public sealed record ImageOverlapMatch(int OverlapRows, double Confidence, int HorizontalOffset = 0);

public static class ImageOverlapMatcher
{
    /// <param name="preferredNeighborhoodOnly">
    /// Restricts the search to the temporal fast path around
    /// <paramref name="preferredNewRows"/> and returns null instead of falling
    /// back to the global search. Callers that only need to confirm or refute
    /// a displacement they already predict — the opposite-direction probe and
    /// the consistency verifications — use this to avoid paying the full
    /// global cost for an answer the neighborhood already decides.
    /// </param>
    public static ImageOverlapMatch? FindVerticalOverlap(
        PixelImage previousFrame,
        PixelImage currentFrame,
        int minimumOverlapRows,
        double minimumConfidence,
        int minimumNewRows = 0,
        int? preferredNewRows = null,
        bool preferredNeighborhoodOnly = false)
    {
        ArgumentNullException.ThrowIfNull(previousFrame);
        ArgumentNullException.ThrowIfNull(currentFrame);

        if (previousFrame.Width != currentFrame.Width ||
            previousFrame.Height != currentFrame.Height)
        {
            throw new ArgumentException("长截图帧尺寸必须一致。");
        }

        if (minimumOverlapRows <= 0 ||
            minimumOverlapRows >= previousFrame.Height)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumOverlapRows));
        }

        if (minimumConfidence is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumConfidence));
        }

        var maximumNewRows = previousFrame.Height - minimumOverlapRows;

        if (minimumNewRows < 0 || minimumNewRows > maximumNewRows)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumNewRows));
        }

        if (preferredNewRows is { } preferred &&
            (preferred < minimumNewRows || preferred > maximumNewRows))
        {
            preferredNewRows = null;
        }

        if (preferredNeighborhoodOnly && preferredNewRows is null)
        {
            return null;
        }

        var previousPixels = PixelBuffer.Create(previousFrame);
        var currentPixels = PixelBuffer.Create(currentFrame);
        // Keep line numbers and chat avatars on the left; ignore only the
        // narrow scrollbar strip on the right and a thin bottom chrome band.
        const int ignoredLeft = 0;
        var ignoredRight = previousFrame.Width >= 80
            ? Math.Clamp(previousFrame.Width / 80, 10, 24)
            : 0;
        var comparisonRight = previousFrame.Width - ignoredRight;
        var ignoredBottom = previousFrame.Height >= 120
            ? Math.Min(24, previousFrame.Height / 30)
            : 0;
        // Stable content bands: full frame for general content, and a band that
        // skips sticky headers. Require candidates to agree across both.
        var topOffsets = new[]
        {
            0,
            previousFrame.Height >= 120 ? previousFrame.Height / 5 : 0,
        }.Distinct().ToArray();

        // Row profiles rank every displacement cheaply and both the temporal and
        // the global stage need them. Building them once keeps a missed fast
        // path from paying for the same scan twice, which matters because a
        // fast scroll misses the fast path on almost every frame.
        var previousProfiles = BuildRowProfiles(
            previousPixels,
            ignoredLeft,
            comparisonRight);
        var currentProfiles = BuildRowProfiles(
            currentPixels,
            ignoredLeft,
            comparisonRight);

        // Stage 0: WeChat-style temporal strip search. Continuous scrolling keeps
        // nearly the same displacement frame-to-frame; resolve that neighborhood
        // first so the interactive path stays inside a single frame budget.
        if (preferredNewRows is { } preferredSeed)
        {
            var fastMatch = TryTemporalFastPath(
                previousPixels,
                currentPixels,
                previousProfiles,
                currentProfiles,
                preferredSeed,
                ignoredLeft,
                comparisonRight,
                ignoredBottom,
                minimumOverlapRows,
                topOffsets,
                minimumNewRows,
                maximumNewRows,
                minimumConfidence,
                previousFrame.Height);

            // When scroll speed changes abruptly, the last displacement can
            // point at a periodic code row. Probe a conservative one-sixth
            // viewport seed only for weak temporal results, then let that
            // stronger local peak guide the global search below.
            var fallbackSeed = Math.Clamp(
                previousFrame.Height / 6,
                minimumNewRows,
                maximumNewRows);
            if ((fastMatch is null || fastMatch.Confidence < 0.97) &&
                Math.Abs(fallbackSeed - preferredSeed) > LocalRefineRadius)
            {
                var fallbackMatch = TryTemporalFastPath(
                    previousPixels,
                    currentPixels,
                    previousProfiles,
                    currentProfiles,
                    fallbackSeed,
                    ignoredLeft,
                    comparisonRight,
                    ignoredBottom,
                    minimumOverlapRows,
                    topOffsets,
                    minimumNewRows,
                    maximumNewRows,
                    minimumConfidence,
                    previousFrame.Height);

                if (fallbackMatch is not null &&
                    (fastMatch is null ||
                     fallbackMatch.Confidence >= fastMatch.Confidence + 0.004))
                {
                    fastMatch = fallbackMatch;
                    preferredNewRows = previousFrame.Height - fallbackMatch.OverlapRows;
                }
            }

            if (preferredNeighborhoodOnly)
            {
                return fastMatch;
            }

            // A moderate local peak is useful as a ranking hint, but repeated
            // code can produce one near the previous displacement when the
            // user abruptly changes scroll speed. Only a strong temporal peak
            // may bypass the global row-profile search.
            if (fastMatch is { Confidence: >= 0.97 })
            {
                return fastMatch;
            }
        }

        // Stage 1: row-profile correlation ranks every displacement quickly.
        // This is closer to commercial long-screenshot strip matching and avoids
        // missing the true peak when sparse coarse sampling hits a flat region.
        var profileCandidates = new List<OverlapCandidate>(
            maximumNewRows - minimumNewRows + 1);

        for (var newRows = minimumNewRows; newRows <= maximumNewRows; newRows++)
        {
            var profileScore = ScoreRowProfiles(
                previousProfiles,
                currentProfiles,
                newRows,
                ignoredBottom,
                minimumOverlapRows,
                topOffsets);

            if (profileScore is { } score)
            {
                profileCandidates.Add(new OverlapCandidate(newRows, score));
            }
        }

        if (profileCandidates.Count == 0)
        {
            return null;
        }

        // A frame that agrees with the previous one nowhere — a torn capture
        // whose halves sit at different scroll positions, a popup, a mid-paint
        // smear — still paid for every dense probe below, and during a fast
        // scroll such frames arrive in runs. When even the best row-profile
        // alignment sits far below the acceptance confidence, no dense probe
        // can rescue it; failing fast here lets the next clean sample get its
        // attempt several times sooner, which is what keeps the anchor within
        // matchable range of the viewport.
        var bestProfileScore = 0d;
        foreach (var profileCandidate in profileCandidates)
        {
            bestProfileScore = Math.Max(
                bestProfileScore,
                profileCandidate.Confidence);
        }

        if (bestProfileScore < minimumConfidence - ProfileFailFastMargin)
        {
            return null;
        }

        // Stage 2: densify around the strongest profile peaks, the temporal
        // prior, and a sparse global backup so periodic content still gets a
        // fair dense re-check.
        var denseProbeRows = SelectDenseProbeRows(
            profileCandidates,
            preferredNewRows,
            minimumNewRows,
            maximumNewRows);
        var candidates = new List<OverlapCandidate>();

        foreach (var probeNewRows in denseProbeRows)
        {
            // Dense ranking must include horizontal micro-search. A 1px DWM /
            // compositor drift otherwise scores near-zero at the true vertical
            // displacement and the stitch latches onto a weaker wrong peak.
            // Ranking pass uses moderate samples; RefineAroundCandidate re-scores
            // the winner with denser sampling. Horizontal micro-search stays on
            // so 1px drift cannot zero out the true vertical peak during ranking.
            var denseComparison = ScoreWithHorizontalMicroSearch(
                previousPixels,
                currentPixels,
                probeNewRows,
                ignoredLeft,
                comparisonRight,
                ignoredBottom,
                minimumOverlapRows,
                topOffsets,
                targetRowSamples: previousFrame.Height >= 360 ? 64 : 80,
                targetColumnSamples: previousFrame.Width >= 300 ? 48 : 36,
                minimumTexture: MinimumReliableTexture);

            if (denseComparison is { } denseMetrics &&
                denseMetrics.Confidence >= minimumConfidence)
            {
                candidates.Add(new OverlapCandidate(
                    probeNewRows,
                    denseMetrics.Confidence,
                    denseMetrics.HorizontalOffset));
            }
        }

        if (candidates.Count == 0)
        {
            return null;
        }

        var bestCandidate = SelectBestCandidate(candidates, preferredNewRows);
        bestCandidate = RefineAroundCandidate(
            previousPixels,
            currentPixels,
            bestCandidate,
            ignoredLeft,
            comparisonRight,
            ignoredBottom,
            minimumOverlapRows,
            topOffsets,
            minimumNewRows,
            maximumNewRows,
            minimumConfidence,
            preferredNewRows);

        if (IsAmbiguousTransitionalMatch(candidates, bestCandidate, preferredNewRows))
        {
            return null;
        }

        var zeroMotionComparison = FindZeroMotionComparison(
            previousPixels,
            currentPixels,
            ignoredLeft,
            comparisonRight,
            ignoredBottom);

        // At a scroll boundary, an animated card, hover state, or changing thumb
        // can make the frame differ just enough to defeat an exact equality test.
        // A periodic card grid can then produce a convincing large shift. Prefer
        // zero displacement when its content score is close to the best shift.
        if (IsProbablyNoMotion(zeroMotionComparison, bestCandidate))
        {
            return null;
        }

        return new ImageOverlapMatch(
            previousFrame.Height - bestCandidate.NewRows,
            bestCandidate.Confidence,
            bestCandidate.HorizontalOffset);
    }

    private const double MinimumReliableTexture = 0.0015;
    private const double MinimumNoMotionConfidence = 0.93;
    private const double MaximumNoMotionConfidenceGap = 0.006;
    private const double MinimumHeaderConfidenceAdvantage = 0.04;
    private const double TemporalConfidenceAdvantage = 0.006;
    private const double TemporalNearConfidenceGap = 0.004;
    private const double AmbiguousPeakConfidenceGap = 0.008;
    private const int MaximumDenseCandidates = 12;
    private const int MinimumTemporalNeighborhoodRadius = 12;
    private const int MaximumTemporalNeighborhoodRadius = 96;
    // Widest neighborhood the temporal fast path will rank. It only bounds the
    // cheap row-profile scan, so a large window costs very little, while a
    // narrow one forced every accelerating frame into the full global search.
    private const int MaximumTemporalScanRadius = 720;
    private const int LocalRefineRadius = 2;
    // How far below the acceptance confidence the best row-profile score may
    // sit before the global stage gives up without dense probing. Real matches
    // keep profile scores close to their dense confidence; only frames with no
    // consistent alignment anywhere fall this far.
    private const double ProfileFailFastMargin = 0.06;
    // Expand dense probes around only the strongest profile peaks. Expanding
    // every top-N peak by ±radius explodes cost on large frames (interactive
    // budget tests target <1s); a few seeds still recover peaks that rank a
    // few rows off under 1px horizontal DWM drift.
    private const int ProfileProbeNeighborhoodRadius = 2;
    private const int ProfileNeighborhoodSeedCount = 4;
    private static readonly int[] HorizontalMicroOffsets = [0, -1, 1];
    private const int TemplateStripRows = 28;
    private const int MinimumTextureWeight = 4;
    private const int MaximumTextureWeight = 256;
    private const int RowProfileColumnSamples = 48;


    private static ImageOverlapMatch? TryTemporalFastPath(
        PixelBuffer previousPixels,
        PixelBuffer currentPixels,
        RowProfile[] previousProfiles,
        RowProfile[] currentProfiles,
        int preferredNewRows,
        int comparisonLeft,
        int comparisonRight,
        int ignoredBottom,
        int minimumOverlapRows,
        IReadOnlyList<int> topOffsets,
        int minimumNewRows,
        int maximumNewRows,
        double minimumConfidence,
        int frameHeight)
    {
        var radius = GetTemporalScanRadius(preferredNewRows);
        var from = Math.Max(minimumNewRows, preferredNewRows - radius);
        var to = Math.Min(maximumNewRows, preferredNewRows + radius);
        if (to < from)
        {
            return null;
        }

        // Prefer a cheap, decisive hit on the prior step before scanning the
        // whole neighborhood. Continuous scroll usually stays on the same
        // displacement; full-radius dense scoring is the fallback.
        var seedColumnSamples = previousPixels.Width >= 300 ? 80 : 48;
        var seedComparison = ScoreWithHorizontalMicroSearch(
            previousPixels,
            currentPixels,
            preferredNewRows,
            comparisonLeft,
            comparisonRight,
            ignoredBottom,
            minimumOverlapRows,
            topOffsets,
            targetRowSamples: previousPixels.Height >= 360 ? 80 : 96,
            targetColumnSamples: seedColumnSamples,
            minimumTexture: MinimumReliableTexture);

        var candidates = new List<OverlapCandidate>(to - from + 1);
        if (seedComparison is { } seedMetrics &&
            seedMetrics.Confidence >= minimumConfidence)
        {
            candidates.Add(new OverlapCandidate(
                preferredNewRows,
                seedMetrics.Confidence,
                seedMetrics.HorizontalOffset));
        }

        // The normal continuous-scroll case should not pay for the whole
        // temporal neighborhood. Recheck only +/-3 rows at full density and
        // return immediately when the previous displacement remains an
        // essentially exact match. This keeps frame processing below the screen
        // refresh interval, so intermediate viewports are not skipped.
        if (seedComparison is { } exactSeed &&
            exactSeed.Confidence >= 0.995)
        {
            var refinedSeed = RefineAroundCandidate(
                previousPixels,
                currentPixels,
                new OverlapCandidate(
                    preferredNewRows,
                    exactSeed.Confidence,
                    exactSeed.HorizontalOffset),
                comparisonLeft,
                comparisonRight,
                ignoredBottom,
                minimumOverlapRows,
                topOffsets,
                minimumNewRows,
                maximumNewRows,
                minimumConfidence,
                preferredNewRows);

            if (refinedSeed.Confidence >= 0.99 &&
                Math.Abs(refinedSeed.NewRows - preferredNewRows) <=
                    LocalRefineRadius)
            {
                var zeroMotion = FindZeroMotionComparison(
                    previousPixels,
                    currentPixels,
                    comparisonLeft,
                    comparisonRight,
                    ignoredBottom);
                if (!IsProbablyNoMotion(zeroMotion, refinedSeed))
                {
                    return new ImageOverlapMatch(
                        frameHeight - refinedSeed.NewRows,
                        refinedSeed.Confidence,
                        refinedSeed.HorizontalOffset);
                }
            }
        }

        // Rank the temporal neighborhood with cheap row profiles first. Dense
        // pixel scoring every possible displacement made one match slower than a
        // display refresh, which caused the sampler to skip the intermediate
        // viewports that make fast scrolling stitchable.
        var temporalProfiles = new List<OverlapCandidate>(to - from + 1);
        for (var newRows = from; newRows <= to; newRows++)
        {
            var profileScore = ScoreRowProfiles(
                previousProfiles,
                currentProfiles,
                newRows,
                ignoredBottom,
                minimumOverlapRows,
                topOffsets);
            if (profileScore is { } score)
            {
                temporalProfiles.Add(new OverlapCandidate(newRows, score));
            }
        }

        var denseRows = new SortedSet<int>();
        foreach (var profile in temporalProfiles
                     .OrderByDescending(candidate => candidate.Confidence)
                     .ThenBy(candidate =>
                         Math.Abs(candidate.NewRows - preferredNewRows))
                     .Take(8))
        {
            denseRows.Add(profile.NewRows);
        }

        foreach (var profile in temporalProfiles
                     .OrderByDescending(candidate => candidate.Confidence)
                     .Take(3))
        {
            for (var candidateRows = Math.Max(from, profile.NewRows - 1);
                 candidateRows <= Math.Min(to, profile.NewRows + 1);
                 candidateRows++)
            {
                denseRows.Add(candidateRows);
            }
        }

        for (var candidateRows = Math.Max(from, preferredNewRows - 4);
             candidateRows <= Math.Min(to, preferredNewRows + 4);
             candidateRows++)
        {
            denseRows.Add(candidateRows);
        }

        // Horizontal micro-search is still required so 1px lateral drift does
        // not zero out the true vertical peak.
        var columnSamples = previousPixels.Width >= 300 ? 56 : 40;
        foreach (var newRows in denseRows)
        {
            if (newRows == preferredNewRows)
            {
                continue;
            }

            var comparison = ScoreWithHorizontalMicroSearch(
                previousPixels,
                currentPixels,
                newRows,
                comparisonLeft,
                comparisonRight,
                ignoredBottom,
                minimumOverlapRows,
                topOffsets,
                targetRowSamples: 56,
                targetColumnSamples: columnSamples,
                minimumTexture: MinimumReliableTexture);

            if (comparison is { } metrics &&
                metrics.Confidence >= minimumConfidence)
            {
                candidates.Add(new OverlapCandidate(
                    newRows,
                    metrics.Confidence,
                    metrics.HorizontalOffset));
            }
        }

        // Near-perfect seed with no competing neighborhood peak: accept after
        // a tight local refine without waiting on weaker distant candidates.
        if (seedComparison is { } strongSeed &&
            strongSeed.Confidence >= 0.99 &&
            candidates.Count > 0)
        {
            var strongCompetitor = false;
            foreach (var candidate in candidates)
            {
                if (candidate.NewRows == preferredNewRows)
                {
                    continue;
                }

                if (candidate.Confidence >= strongSeed.Confidence - 0.01 &&
                    Math.Abs(candidate.NewRows - preferredNewRows) > LocalRefineRadius)
                {
                    strongCompetitor = true;
                    break;
                }
            }

            if (!strongCompetitor)
            {
                // Keep only the seed (+ later refine) so decisive peaks stay interactive.
                candidates.RemoveAll(candidate => candidate.NewRows != preferredNewRows);
                if (candidates.Count == 0)
                {
                    candidates.Add(new OverlapCandidate(
                        preferredNewRows,
                        strongSeed.Confidence,
                        strongSeed.HorizontalOffset));
                }
            }
        }

        if (candidates.Count == 0)
        {
            return null;
        }

        var bestCandidate = SelectBestCandidate(candidates, preferredNewRows);
        bestCandidate = RefineAroundCandidate(
            previousPixels,
            currentPixels,
            bestCandidate,
            comparisonLeft,
            comparisonRight,
            ignoredBottom,
            minimumOverlapRows,
            topOffsets,
            minimumNewRows,
            maximumNewRows,
            minimumConfidence,
            preferredNewRows);

        if (IsAmbiguousTransitionalMatch(candidates, bestCandidate, preferredNewRows))
        {
            return null;
        }

        // Reject weak local peaks so a sudden large step falls through to the
        // full multi-stage search instead of latching onto a nearby period.
        if (!IsDecisiveTemporalPeak(candidates, bestCandidate, preferredNewRows, minimumConfidence))
        {
            return null;
        }

        var zeroMotionComparison = FindZeroMotionComparison(
            previousPixels,
            currentPixels,
            comparisonLeft,
            comparisonRight,
            ignoredBottom);

        if (IsProbablyNoMotion(zeroMotionComparison, bestCandidate))
        {
            return null;
        }

        return new ImageOverlapMatch(
            frameHeight - bestCandidate.NewRows,
            bestCandidate.Confidence,
            bestCandidate.HorizontalOffset);
    }

    private static bool IsDecisiveTemporalPeak(
        IReadOnlyList<OverlapCandidate> candidates,
        OverlapCandidate bestCandidate,
        int preferredNewRows,
        double minimumConfidence)
    {
        // Near-perfect strip matches are safe to accept immediately.
        if (bestCandidate.Confidence >= 0.99)
        {
            return true;
        }

        // Prefer staying close to the previous step. A peak far from the prior
        // inside the neighborhood is usually a period of repeated content.
        var distance = Math.Abs(bestCandidate.NewRows - preferredNewRows);
        var radius = GetTemporalNeighborhoodRadius(preferredNewRows);
        if (distance > Math.Max(4, radius / 3) &&
            bestCandidate.Confidence < minimumConfidence + 0.02)
        {
            return false;
        }

        if (candidates.Count == 1)
        {
            return bestCandidate.Confidence >= Math.Min(0.99, minimumConfidence + 0.01);
        }

        var second = candidates
            .Where(candidate => candidate.NewRows != bestCandidate.NewRows)
            .OrderByDescending(candidate => candidate.Confidence)
            .FirstOrDefault();

        if (second is null)
        {
            return true;
        }

        var gap = bestCandidate.Confidence - second.Confidence;
        var secondDistance = Math.Abs(second.NewRows - preferredNewRows);

        // Unique peak, or the runner-up is farther from the temporal prior.
        return gap >= 0.012 ||
               (gap >= 0.004 && distance <= secondDistance);
    }
    private static int[] SelectDenseProbeRows(
        IReadOnlyList<OverlapCandidate> profileCandidates,
        int? preferredNewRows,
        int minimumNewRows,
        int maximumNewRows)
    {
        var probes = new SortedSet<int>();
        var orderedProfiles = profileCandidates
            .OrderByDescending(candidate => candidate.Confidence)
            .ThenBy(candidate =>
                preferredNewRows is { } preferred
                    ? Math.Abs(candidate.NewRows - preferred)
                    : candidate.NewRows)
            .ThenBy(candidate => candidate.NewRows)
            .ToArray();

        // Exact profile peaks (fast ranking set).
        foreach (var candidate in orderedProfiles.Take(MaximumDenseCandidates))
        {
            probes.Add(candidate.NewRows);
        }

        // Local neighborhoods only around the strongest few peaks so a true
        // displacement that ranks a few rows off still receives dense scoring
        // with horizontal micro-search, without densifying the whole top-N set.
        foreach (var candidate in orderedProfiles.Take(ProfileNeighborhoodSeedCount))
        {
            var from = Math.Max(minimumNewRows, candidate.NewRows - ProfileProbeNeighborhoodRadius);
            var to = Math.Min(maximumNewRows, candidate.NewRows + ProfileProbeNeighborhoodRadius);

            for (var newRows = from; newRows <= to; newRows++)
            {
                probes.Add(newRows);
            }
        }

        // Also retain the strongest small-motion peaks. A user can stop a fast
        // fling with one short wheel tick; without this band, a large previous
        // displacement can crowd the true 10-30px peak out of the dense set.
        var lowMotionLimit = Math.Min(
            maximumNewRows,
            Math.Max(96, (maximumNewRows - minimumNewRows) / 3));
        foreach (var candidate in orderedProfiles
                     .Where(candidate => candidate.NewRows <= lowMotionLimit)
                     .Take(4))
        {
            var from = Math.Max(
                minimumNewRows,
                candidate.NewRows - ProfileProbeNeighborhoodRadius);
            var to = Math.Min(
                maximumNewRows,
                candidate.NewRows + ProfileProbeNeighborhoodRadius);
            for (var newRows = from; newRows <= to; newRows++)
            {
                probes.Add(newRows);
            }
        }

        // Sparse four-row lattice guarantees that very small real motion gets
        // a dense pixel check even when antialiasing makes its row profile rank
        // poorly. Refinement recovers the exact row within +/-3 pixels.
        var lowMotionProbeLimit = Math.Min(lowMotionLimit, 96);
        for (var newRows = minimumNewRows;
             newRows <= lowMotionProbeLimit;
             newRows += newRows < 32 ? 2 : 6)
        {
            probes.Add(newRows);
        }

        if (preferredNewRows is { } preferredRows)
        {
            var radius = GetTemporalNeighborhoodRadius(preferredRows);
            var localRadius = Math.Min(10, Math.Max(4, radius / 4));
            var from = Math.Max(minimumNewRows, preferredRows - localRadius);
            var to = Math.Min(maximumNewRows, preferredRows + localRadius);

            for (var newRows = from; newRows <= to; newRows += 2)
            {
                probes.Add(newRows);
            }

            probes.Add(Math.Clamp(preferredRows, minimumNewRows, maximumNewRows));

            foreach (var candidate in orderedProfiles
                         .Where(candidate =>
                             Math.Abs(candidate.NewRows - preferredRows) <= radius)
                         .Take(6))
            {
                probes.Add(candidate.NewRows);
            }
        }

        // Global lattice safety net. Keep step fine enough that common first-
        // frame displacements (e.g. 36 with minNewRows=8) land on a probe:
        // step 8 from 8 yields 8,16,24,32,40 and skips 36. Prefer a slightly
        // denser lattice only when there is no temporal prior; with a prior and
        // a nearby profile peak, skip distant lattice points for interactivity.
        var preferLocalLattice = false;
        if (preferredNewRows is { } preferredForLattice &&
            orderedProfiles.Length > 0)
        {
            var radius = GetTemporalNeighborhoodRadius(preferredForLattice);
            preferLocalLattice =
                Math.Abs(orderedProfiles[0].NewRows - preferredForLattice) <= radius;
        }

        if (!preferLocalLattice)
        {
            var range = Math.Max(1, maximumNewRows - minimumNewRows);
            // Keep first-frame lattices fine enough to hit common steps (36 with
            // minNewRows=8 needs step| (36-8)), but not so dense that a 640x480
            // cold match exceeds the interactive budget.
            var latticeStep = preferredNewRows is null
                ? Math.Max(4, range / 36)
                : Math.Max(8, range / 28);

            for (var newRows = minimumNewRows; newRows <= maximumNewRows; newRows += latticeStep)
            {
                probes.Add(newRows);
            }

            probes.Add(maximumNewRows);
        }

        return probes.ToArray();
    }

    private static OverlapCandidate SelectBestCandidate(
        IReadOnlyList<OverlapCandidate> candidates,
        int? preferredNewRows)
    {
        // Near-equal confidences are treated as a tie so continuous scrolling
        // prefers the smaller displacement instead of a repeated-content period.
        var orderedCandidates = candidates
            .OrderByDescending(candidate =>
                Math.Round(candidate.Confidence / TemporalNearConfidenceGap) *
                TemporalNearConfidenceGap)
            .ThenBy(candidate =>
                preferredNewRows is { } preferred
                    ? Math.Abs(candidate.NewRows - preferred)
                    : candidate.NewRows)
            .ThenBy(candidate => candidate.NewRows)
            .ThenByDescending(candidate => candidate.Confidence)
            .ToArray();
        var bestCandidate = orderedCandidates[0];

        if (preferredNewRows is not { } preferred || orderedCandidates.Length == 1)
        {
            return bestCandidate;
        }

        // Continuous scrolling almost never jumps by a large unrelated period of
        // repeated content. When a near-equal peak sits close to the previous
        // displacement, prefer that temporally consistent answer.
        OverlapCandidate? temporallyPreferred = null;

        foreach (var candidate in orderedCandidates)
        {
            var distance = Math.Abs(candidate.NewRows - preferred);

            if (distance > GetTemporalNeighborhoodRadius(preferred))
            {
                continue;
            }

            if (bestCandidate.Confidence - candidate.Confidence >
                TemporalConfidenceAdvantage)
            {
                continue;
            }

            if (temporallyPreferred is null ||
                distance < Math.Abs(temporallyPreferred.NewRows - preferred) ||
                (distance == Math.Abs(temporallyPreferred.NewRows - preferred) &&
                 candidate.Confidence > temporallyPreferred.Confidence))
            {
                temporallyPreferred = candidate;
            }
        }

        if (temporallyPreferred is not null &&
            (Math.Abs(bestCandidate.NewRows - preferred) > GetTemporalNeighborhoodRadius(preferred) ||
             bestCandidate.Confidence - temporallyPreferred.Confidence <=
                 TemporalNearConfidenceGap))
        {
            return temporallyPreferred;
        }

        return bestCandidate;
    }

    private static RowProfile[] BuildRowProfiles(
        PixelBuffer frame,
        int comparisonLeft,
        int comparisonRight)
    {
        var width = comparisonRight - comparisonLeft;

        if (width <= 0)
        {
            return Array.Empty<RowProfile>();
        }

        var columnStep = Math.Max(1, width / RowProfileColumnSamples);
        var anchorRight = comparisonLeft + Math.Max(48, width / 6);
        var profiles = new RowProfile[frame.Height];

        for (var y = 0; y < frame.Height; y++)
        {
            long sumR = 0;
            long sumG = 0;
            long sumB = 0;
            long sumEdge = 0;
            var sampleWeightTotal = 0;
            byte previousLuma = 0;
            var hasPrevious = false;

            for (var x = comparisonLeft; x < comparisonRight; x += columnStep)
            {
                frame.GetRgb(x, y, out var r, out var g, out var b);
                // Line numbers, avatars and tree markers usually live in the
                // leading band. Without extra weight, repetitive editor/chat
                // bodies dominate the row profile and the real displacement is
                // discarded before the dense pixel comparison ever sees it.
                var sampleWeight = x < anchorRight ? 4 : 1;
                sumR += r * sampleWeight;
                sumG += g * sampleWeight;
                sumB += b * sampleWeight;
                var luma = (byte)((r * 30 + g * 59 + b * 11) / 100);

                if (hasPrevious)
                {
                    sumEdge += Math.Abs(luma - previousLuma) * sampleWeight;
                }

                previousLuma = luma;
                hasPrevious = true;
                sampleWeightTotal += sampleWeight;
            }

            if (sampleWeightTotal == 0)
            {
                profiles[y] = default;
                continue;
            }

            profiles[y] = new RowProfile(
                (float)(sumR / (double)sampleWeightTotal),
                (float)(sumG / (double)sampleWeightTotal),
                (float)(sumB / (double)sampleWeightTotal),
                (float)(sumEdge / (double)Math.Max(1, sampleWeightTotal - 1)));
        }

        return profiles;
    }

    private static double? ScoreRowProfiles(
        RowProfile[] previousProfiles,
        RowProfile[] currentProfiles,
        int newRows,
        int ignoredBottom,
        int minimumOverlapRows,
        IReadOnlyList<int> topOffsets)
    {
        if (previousProfiles.Length == 0 ||
            previousProfiles.Length != currentProfiles.Length)
        {
            return null;
        }

        var height = previousProfiles.Length;
        double bestScore = 0;
        var found = false;

        foreach (var comparisonTop in topOffsets)
        {
            var comparisonBottom = height - ignoredBottom - newRows;

            if (comparisonBottom - comparisonTop < minimumOverlapRows)
            {
                continue;
            }

            double totalWeightedDifference = 0;
            double totalWeight = 0;
            double totalTexture = 0;

            for (var y = comparisonTop; y < comparisonBottom; y++)
            {
                var previous = previousProfiles[y + newRows];
                var current = currentProfiles[y];
                var texture = Math.Max(previous.Edge, current.Edge) +
                              Math.Abs(previous.R - previous.G) +
                              Math.Abs(previous.G - previous.B) +
                              1d;
                var difference =
                    Math.Abs(previous.R - current.R) +
                    Math.Abs(previous.G - current.G) +
                    Math.Abs(previous.B - current.B) +
                    Math.Abs(previous.Edge - current.Edge);
                totalWeightedDifference += difference * texture;
                totalWeight += texture;
                totalTexture += texture;
            }

            if (totalWeight <= 0 || totalTexture / (comparisonBottom - comparisonTop) < 2d)
            {
                continue;
            }

            // RGB (3 * 255) + edge (255)
            var score = 1d - (totalWeightedDifference / (totalWeight * 255d * 4d));
            bestScore = found ? Math.Max(bestScore, score) : score;
            found = true;
        }

        return found ? bestScore : null;
    }

    private static int GetTemporalNeighborhoodRadius(int preferredNewRows)
    {
        // Real wheel + inertia often changes the step by tens of pixels between
        // samples. Keep the prior wide enough to stay sticky without forcing a
        // doubled period of repeated content.
        var preferred = Math.Max(0, preferredNewRows);
        var radius = Math.Max(
            MinimumTemporalNeighborhoodRadius,
            (preferred * 3) / 4);
        return Math.Clamp(
            radius,
            MinimumTemporalNeighborhoodRadius,
            MaximumTemporalNeighborhoodRadius);
    }

    /// <summary>
    /// Neighborhood the temporal fast path ranks around the previous
    /// displacement. It is deliberately much wider than
    /// <see cref="GetTemporalNeighborhoodRadius"/>, which still governs how
    /// strongly a nearby candidate is preferred.
    /// </summary>
    /// <remarks>
    /// A fling accelerates from a few pixels per frame to several hundred, so
    /// two consecutive samples routinely differ by far more than the ±96 row
    /// preference window. Ranking only that window meant every frame of a fast
    /// scroll missed the fast path and paid for the full global search, which
    /// is several times slower — so the sampler fell further behind on each
    /// frame until the remaining displacement exceeded one viewport and the
    /// stitch stopped growing. Widening only the scan keeps those frames on the
    /// cheap path without loosening any acceptance rule.
    /// <para>
    /// Twice the previous displacement covers real steps up to three times the
    /// estimate, which spans the whole acceleration phase of a fling. Measured
    /// on a 1200x900 viewport, that takes a 420-row step with a 150-row prior
    /// from 401 scored alignments down to 80, and a 700-row step with a 260-row
    /// prior from 464 down to 80 — the same cost as an ordinary steady scroll —
    /// while resolving the identical displacement.
    /// </para>
    /// </remarks>
    private static int GetTemporalScanRadius(int preferredNewRows)
    {
        var preferred = Math.Max(0, preferredNewRows);
        return Math.Clamp(
            Math.Max(MinimumTemporalNeighborhoodRadius, preferred * 2),
            MinimumTemporalNeighborhoodRadius,
            MaximumTemporalScanRadius);
    }

    private static OverlapCandidate RefineAroundCandidate(
        PixelBuffer previousPixels,
        PixelBuffer currentPixels,
        OverlapCandidate seed,
        int comparisonLeft,
        int comparisonRight,
        int ignoredBottom,
        int minimumOverlapRows,
        IReadOnlyList<int> topOffsets,
        int minimumNewRows,
        int maximumNewRows,
        double minimumConfidence,
        int? preferredNewRows)
    {
        var refined = new List<OverlapCandidate>();
        var from = Math.Max(minimumNewRows, seed.NewRows - LocalRefineRadius);
        var to = Math.Min(maximumNewRows, seed.NewRows + LocalRefineRadius);
        var columnSamples = previousPixels.Width >= 300 ? 96 : 72;

        for (var newRows = from; newRows <= to; newRows++)
        {
            // Re-score the seed too with denser sampling and horizontal micro-search
            // so a 1px compositor drift does not leave a visible seam.
            var bestForRows = ScoreWithHorizontalMicroSearch(
                previousPixels,
                currentPixels,
                newRows,
                comparisonLeft,
                comparisonRight,
                ignoredBottom,
                minimumOverlapRows,
                topOffsets,
                targetRowSamples: 128,
                targetColumnSamples: columnSamples,
                minimumTexture: MinimumReliableTexture);

            if (bestForRows is { } denseMetrics &&
                denseMetrics.Confidence >= minimumConfidence)
            {
                refined.Add(new OverlapCandidate(newRows, denseMetrics.Confidence, denseMetrics.HorizontalOffset));
            }
        }

        if (refined.Count == 0)
        {
            return seed;
        }

        return SelectBestCandidate(refined, preferredNewRows);
    }

    private static ScoredAlignment? ScoreWithHorizontalMicroSearch(
        PixelBuffer previousPixels,
        PixelBuffer currentPixels,
        int newRows,
        int comparisonLeft,
        int comparisonRight,
        int ignoredBottom,
        int minimumOverlapRows,
        IReadOnlyList<int> topOffsets,
        int targetRowSamples,
        int targetColumnSamples,
        double minimumTexture)
    {
        ScoredAlignment? best = null;

        foreach (var horizontalOffset in HorizontalMicroOffsets)
        {
            var comparison = FindBestComparison(
                previousPixels,
                currentPixels,
                newRows,
                comparisonLeft,
                comparisonRight,
                ignoredBottom,
                minimumOverlapRows,
                topOffsets,
                targetRowSamples,
                targetColumnSamples,
                minimumTexture,
                horizontalOffset);

            if (comparison is null)
            {
                continue;
            }

            if (best is null ||
                comparison.Value.Confidence > best.Value.Confidence ||
                (comparison.Value.Confidence == best.Value.Confidence &&
                 Math.Abs(horizontalOffset) < Math.Abs(best.Value.HorizontalOffset)))
            {
                best = new ScoredAlignment(
                    comparison.Value.Confidence,
                    comparison.Value.Texture,
                    horizontalOffset);
            }

            // Common case: no lateral drift. A near-perfect h=0 match means the
            // ±1 offsets cannot improve the stitch and only triple the cost of
            // the interactive temporal / dense paths.
            if (horizontalOffset == 0 &&
                best is { } early &&
                early.HorizontalOffset == 0 &&
                early.Confidence >= 0.998)
            {
                return early;
            }
        }

        return best;
    }

    private static bool IsAmbiguousTransitionalMatch(
        IReadOnlyList<OverlapCandidate> candidates,
        OverlapCandidate bestCandidate,
        int? preferredNewRows)
    {
        // Mid-scroll compositor frames often produce two weak peaks far apart.
        // Without temporal support those frames tear the stitch; drop them and
        // wait for the next settled sample instead.
        if (candidates.Count < 2)
        {
            return false;
        }

        var ordered = candidates
            .OrderByDescending(candidate => candidate.Confidence)
            .ThenBy(candidate => candidate.NewRows)
            .ToArray();
        var second = ordered.FirstOrDefault(candidate =>
            candidate.NewRows != bestCandidate.NewRows);

        if (second is null)
        {
            return false;
        }

        var confidenceGap = bestCandidate.Confidence - second.Confidence;
        var displacementGap = Math.Abs(bestCandidate.NewRows - second.NewRows);
        if (confidenceGap > AmbiguousPeakConfidenceGap || displacementGap < 12)
        {
            return false;
        }

        if (preferredNewRows is { } preferred)
        {
            var radius = GetTemporalNeighborhoodRadius(preferred);
            if (Math.Abs(bestCandidate.NewRows - preferred) <= radius)
            {
                return false;
            }
        }

        // Only reject weak winners. A clearly dominant match is kept even when
        // a distant runner-up exists.
        return bestCandidate.Confidence < 0.99;
    }

    private static ComparisonResult? FindBestComparison(
        PixelBuffer previousFrame,
        PixelBuffer currentFrame,
        int newRows,
        int comparisonLeft,
        int comparisonRight,
        int ignoredBottom,
        int minimumOverlapRows,
        IEnumerable<int> topOffsets,
        int targetRowSamples,
        int targetColumnSamples,
        double minimumTexture,
        int horizontalOffset = 0,
        bool useStripEvidence = true)
    {
        var comparisons = new List<(int Top, ComparisonResult Metrics)>();

        foreach (var comparisonTop in topOffsets)
        {
            var comparisonBottom =
                previousFrame.Height - ignoredBottom - newRows;

            if (comparisonBottom - comparisonTop < minimumOverlapRows)
            {
                continue;
            }

            var comparison = CompareShiftedContent(
                previousFrame,
                currentFrame,
                newRows,
                comparisonLeft,
                comparisonRight,
                comparisonTop,
                comparisonBottom,
                targetRowSamples,
                targetColumnSamples,
                horizontalOffset);

            var anchorRight = Math.Min(
                comparisonRight,
                comparisonLeft + Math.Max(
                    48,
                    (comparisonRight - comparisonLeft) / 6));
            if (anchorRight - comparisonLeft >= 24)
            {
                var anchorComparison = CompareShiftedContent(
                    previousFrame,
                    currentFrame,
                    newRows,
                    comparisonLeft,
                    anchorRight,
                    comparisonTop,
                    comparisonBottom,
                    targetRowSamples,
                    Math.Min(48, targetColumnSamples),
                    horizontalOffset);

                if (anchorComparison.Texture >= minimumTexture / 2)
                {
                    comparison = new ComparisonResult(
                        (comparison.Confidence * 0.45) +
                            (anchorComparison.Confidence * 0.55),
                        Math.Max(comparison.Texture, anchorComparison.Texture));
                }
            }

            // Commercial long-screenshot tools primarily match a stable mid-strip
            // of the overlap. Blending that evidence suppresses sticky chrome and
            // repeated card edges that only agree on a thin band. Dense ranking
            // skips this for latency; local refine keeps it for stitch precision.
            if (useStripEvidence)
            {
                var stripComparison = CompareOverlapStrip(
                    previousFrame,
                    currentFrame,
                    newRows,
                    comparisonLeft,
                    comparisonRight,
                    comparisonTop,
                    comparisonBottom,
                    targetColumnSamples,
                    horizontalOffset);

                if (stripComparison.Texture >= minimumTexture / 2)
                {
                    comparison = new ComparisonResult(
                        (comparison.Confidence * 0.62) +
                            (stripComparison.Confidence * 0.38),
                        Math.Max(comparison.Texture, stripComparison.Texture));
                }
            }

            if (comparison.Texture < minimumTexture)
            {
                continue;
            }

            comparisons.Add((comparisonTop, comparison));
        }

        if (comparisons.Count == 0)
        {
            return null;
        }

        var fullComparison = comparisons
            .OrderBy(comparison => comparison.Top)
            .First();
        var bestComparison = comparisons
            .OrderByDescending(comparison => comparison.Metrics.Confidence)
            .First();

        // A better lower band usually means sticky top chrome. Prefer it whenever
        // it is competitive: mid-strip blending can inflate the full-band score
        // enough that a large advantage threshold would incorrectly dilute a
        // near-perfect content match back under the caller confidence gate.
        if (bestComparison.Top > fullComparison.Top)
        {
            var headerGap =
                bestComparison.Metrics.Confidence -
                fullComparison.Metrics.Confidence;

            if (headerGap >= 0 ||
                headerGap >= -MinimumHeaderConfidenceAdvantage * 0.25)
            {
                return bestComparison.Metrics;
            }
        }

        return new ComparisonResult(
            (fullComparison.Metrics.Confidence * 0.7) +
                (bestComparison.Metrics.Confidence * 0.3),
            Math.Max(
                fullComparison.Metrics.Texture,
                bestComparison.Metrics.Texture));
    }

    private static ComparisonResult? FindZeroMotionComparison(
        PixelBuffer previousFrame,
        PixelBuffer currentFrame,
        int comparisonLeft,
        int comparisonRight,
        int ignoredBottom)
    {
        var comparisonTop = previousFrame.Height >= 120
            ? previousFrame.Height / 5
            : 0;
        var comparisonBottom = previousFrame.Height - ignoredBottom;

        if (comparisonBottom <= comparisonTop)
        {
            return null;
        }

        var comparison = CompareShiftedContent(
            previousFrame,
            currentFrame,
            0,
            comparisonLeft,
            comparisonRight,
            comparisonTop,
            comparisonBottom,
            targetRowSamples: 320,
            targetColumnSamples: 96);

        // Repetitive editor bodies can make an unchanged-frame comparison look
        // almost as good as a real scroll. Reuse the same leading anchor band as
        // the overlap scorer so unique line numbers and tree markers can veto
        // that false no-motion result.
        var anchorRight = Math.Min(
            comparisonRight,
            comparisonLeft + Math.Max(
                48,
                (comparisonRight - comparisonLeft) / 6));
        if (anchorRight - comparisonLeft >= 24)
        {
            var anchorComparison = CompareShiftedContent(
                previousFrame,
                currentFrame,
                0,
                comparisonLeft,
                anchorRight,
                comparisonTop,
                comparisonBottom,
                targetRowSamples: 320,
                targetColumnSamples: 64);

            // A small caret or hover animation should not veto the no-motion
            // result. Require the leading band to disagree across a meaningful
            // portion of its samples before treating it as a real scroll.
            if (anchorComparison.Texture >= MinimumReliableTexture / 2 &&
                anchorComparison.ChangedSampleRatio >= 0.03)
            {
                comparison = new ComparisonResult(
                    (comparison.Confidence * 0.45) +
                        (anchorComparison.Confidence * 0.55),
                    Math.Max(comparison.Texture, anchorComparison.Texture));
            }
        }

        return comparison.Texture >= MinimumReliableTexture
            ? comparison
            : null;
    }

    private static bool IsProbablyNoMotion(
        ComparisonResult? zeroMotionComparison,
        OverlapCandidate bestCandidate)
    {
        return zeroMotionComparison is { } zeroMotion &&
               zeroMotion.Confidence >= MinimumNoMotionConfidence &&
               zeroMotion.Confidence >=
                   bestCandidate.Confidence - MaximumNoMotionConfidenceGap;
    }

    private static ComparisonResult CompareOverlapStrip(
        PixelBuffer previousFrame,
        PixelBuffer currentFrame,
        int newRows,
        int comparisonLeft,
        int comparisonRight,
        int comparisonTop,
        int comparisonBottom,
        int targetColumnSamples,
        int horizontalOffset = 0)
    {
        var overlapHeight = comparisonBottom - comparisonTop;
        if (overlapHeight <= 0)
        {
            return default;
        }

        // Commercial tools correlate short horizontal templates. Use a primary
        // mid-strip plus a secondary strip closer to the advancing edge so blank
        // mid bands (chat padding) cannot dominate the score alone.
        var stripRows = Math.Min(TemplateStripRows, Math.Max(8, overlapHeight / 3));
        var denseColumns = Math.Max(targetColumnSamples, Math.Min(192, comparisonRight - comparisonLeft));
        var primaryTop = comparisonTop + Math.Max(0, (overlapHeight - stripRows) / 2);
        var primary = CompareShiftedContent(
            previousFrame,
            currentFrame,
            newRows,
            comparisonLeft,
            comparisonRight,
            primaryTop,
            primaryTop + stripRows,
            targetRowSamples: Math.Max(stripRows, 1),
            targetColumnSamples: denseColumns,
            horizontalOffset);

        if (overlapHeight < stripRows * 2 + 4)
        {
            return primary;
        }

        // Prefer the strip nearer the newly revealed content (bottom of previous /
        // top of current for downward scroll), which is what WeChat-style template
        // matching locks onto when the viewport advances.
        var edgeTop = comparisonBottom - stripRows;
        var edge = CompareShiftedContent(
            previousFrame,
            currentFrame,
            newRows,
            comparisonLeft,
            comparisonRight,
            edgeTop,
            comparisonBottom,
            targetRowSamples: Math.Max(stripRows, 1),
            targetColumnSamples: denseColumns,
            horizontalOffset);

        if (edge.Texture < primary.Texture * 0.5)
        {
            return primary;
        }

        return new ComparisonResult(
            (primary.Confidence * 0.55) + (edge.Confidence * 0.45),
            Math.Max(primary.Texture, edge.Texture));
    }

    private static ComparisonResult CompareShiftedContent(
        PixelBuffer previousFrame,
        PixelBuffer currentFrame,
        int newRows,
        int comparisonLeft,
        int comparisonRight,
        int comparisonTop,
        int comparisonBottom,
        int targetRowSamples,
        int targetColumnSamples,
        int horizontalOffset = 0)
    {
        long totalWeightedDifference = 0;
        long totalWeight = 0;
        long totalTexture = 0;
        var samples = 0;
        var changedSamples = 0;
        var textureSamples = 0;
        var rowStep = Math.Max(
            1,
            (comparisonBottom - comparisonTop) / targetRowSamples);
        var columnStep = Math.Max(
            1,
            (comparisonRight - comparisonLeft) / targetColumnSamples);
        // previous[x] aligns to current[x + horizontalOffset]; keep both in bounds.
        var sampleLeft = comparisonLeft + Math.Max(0, -horizontalOffset);
        var sampleRight = comparisonRight - Math.Max(0, horizontalOffset);
        var currentRight = comparisonRight;
        if (sampleRight - sampleLeft < 8 || comparisonBottom <= comparisonTop)
        {
            return default;
        }

        for (var y = comparisonTop; y < comparisonBottom; y += rowStep)
        {
            for (var x = sampleLeft; x < sampleRight; x += columnStep)
            {
                var currentX = x + horizontalOffset;
                var difference = previousFrame.GetColorDifference(
                    x,
                    y + newRows,
                    currentFrame,
                    currentX,
                    y);
                if (difference > 36)
                {
                    changedSamples++;
                }
                var localTexture = 0;

                if (currentX + columnStep < currentRight)
                {
                    localTexture += currentFrame.GetColorDifference(
                        currentX,
                        y,
                        currentFrame,
                        currentX + columnStep,
                        y);
                }

                if (y + rowStep < comparisonBottom)
                {
                    localTexture += currentFrame.GetColorDifference(
                        currentX,
                        y,
                        currentFrame,
                        currentX,
                        y + rowStep);
                }

                // Chat panes, feeds and terminals commonly contain large
                // uniform backgrounds. Weight differences by local edge/texture
                // so text, avatars and images decide the alignment instead.
                var weight = Math.Clamp(
                    localTexture,
                    MinimumTextureWeight,
                    MaximumTextureWeight);
                totalWeightedDifference += difference * weight;
                totalWeight += weight;
                totalTexture += localTexture;
                samples++;
                textureSamples++;
            }
        }

        if (samples == 0 || textureSamples == 0 || totalWeight == 0)
        {
            return default;
        }

        var maximumDifference = totalWeight * 255d * 3d;
        var maximumTexture = textureSamples * 255d * 3d;
        return new ComparisonResult(
            1d - (totalWeightedDifference / maximumDifference),
            totalTexture / maximumTexture,
            changedSamples / (double)samples);
    }

    private readonly record struct ComparisonResult(
        double Confidence,
        double Texture,
        double ChangedSampleRatio = 0);

    private readonly record struct ScoredAlignment(
        double Confidence,
        double Texture,
        int HorizontalOffset);

    private readonly record struct RowProfile(float R, float G, float B, float Edge);

    private sealed record OverlapCandidate(int NewRows, double Confidence, int HorizontalOffset = 0);

    private sealed class PixelBuffer
    {
        private const int BytesPerPixel = 4;
        private readonly byte[] _pixels;
        private readonly int _rowLength;

        private PixelBuffer(byte[] pixels, int rowLength)
        {
            _pixels = pixels;
            _rowLength = rowLength;
        }

        public int Height => _pixels.Length / _rowLength;

        public int Width => _rowLength / BytesPerPixel;

        public static PixelBuffer Create(PixelImage image)
        {
            // PixelImage is already tightly packed BGRA with stride = width*4,
            // which is exactly this buffer's layout — share it without copying.
            return new PixelBuffer(image.Pixels, image.Stride);
        }

        public void GetRgb(int x, int y, out byte r, out byte g, out byte b)
        {
            var offset = (y * _rowLength) + (x * BytesPerPixel);
            b = _pixels[offset];
            g = _pixels[offset + 1];
            r = _pixels[offset + 2];
        }

        public int GetColorDifference(
            int x,
            int y,
            PixelBuffer other,
            int otherX,
            int otherY)
        {
            var offset = (y * _rowLength) + (x * BytesPerPixel);
            var otherOffset =
                (otherY * other._rowLength) + (otherX * BytesPerPixel);
            return Math.Abs(_pixels[offset] - other._pixels[otherOffset]) +
                   Math.Abs(_pixels[offset + 1] - other._pixels[otherOffset + 1]) +
                   Math.Abs(_pixels[offset + 2] - other._pixels[otherOffset + 2]);
        }
    }
}
