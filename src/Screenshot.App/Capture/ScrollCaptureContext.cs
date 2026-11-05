using System.Drawing;
using System.Drawing.Imaging;

namespace Screenshot.App.Capture;

// Position is measured from surrounding pixels, never from wheel counts. A
// missing position explicitly marks an unverified frame in this same session.
internal sealed record ScrollCapturePosition(object Session, int? Rows)
{
    internal static ScrollCapturePosition? Read(Bitmap frame) => frame.Tag as ScrollCapturePosition;

    internal static int? Difference(Bitmap reference, Bitmap frame) =>
        Read(reference) is { Rows: { } start } first &&
        Read(frame) is { Rows: { } end } last && ReferenceEquals(first.Session, last.Session)
            ? end - start : null;
}

/// <summary>Uses a taller strip of the same window to locate small selections.</summary>
internal sealed class ScrollCaptureContext : IDisposable
{
    private readonly object _session = new();
    private readonly ScreenRegion _region;
    private readonly Rectangle _selection;
    private ManualScrollFrame? _previous;
    private readonly bool _preferSelectedContent;
    private ManualScrollFrame? _previousSelection;
    private int _position;
    private int _fixedTop;
    private int _fixedBottom;

    internal ScrollCaptureContext(ScreenRegion region, ScreenRegion selection, bool preferSelectedContent = false)
    {
        _region = region;
        _preferSelectedContent = preferSelectedContent;
        _selection = new Rectangle(selection.X - region.X, selection.Y - region.Y,
            selection.Width, selection.Height);
    }

    internal static ScrollCaptureContext? Create(ScrollCaptureTarget target, bool preferSelectedContent = false)
    {
        if (!target.AllowContextCapture) return null;
        if (!ForegroundWindowCaptureService.TryGetClientScreenRegion(target.ScrollTargetHandle, out var client) &&
            !ForegroundWindowCaptureService.TryGetClientScreenRegion(target.WindowHandle, out client)) return null;
        var selection = target.CaptureRegion;
        var visible = ScreenRegion.Intersect(client, VirtualScreen.GetBounds());
        var strip = ScreenRegion.Intersect(visible,
            new ScreenRegion(selection.X, visible.Y, selection.Width, visible.Height));
        if (ScreenRegion.Intersect(strip, selection) != selection ||
            strip.Height < selection.Height + Math.Max(120, selection.Height / 2)) return null;
        return new ScrollCaptureContext(strip, selection, preferSelectedContent);
    }

    internal bool Seed(Bitmap initial, ScrollCaptureTarget target)
    {
        using var surrounding = ForegroundWindowCaptureService.CaptureScrollTargetRegion(
            target with { CaptureRegion = _region, Context = null });
        using var selected = surrounding.Clone(_selection, PixelFormat.Format32bppPArgb);
        using var oldPixels = new ManualScrollFrame(initial);
        using var newPixels = new ManualScrollFrame(selected);
        // Preserve the pre-scroll snapshot. If the page already moved, do not
        // label it with the position of a different live frame.
        if (!oldPixels.IsExactOverlap(newPixels, 0, 0, initial.Height)) return false;
        _previous = new ManualScrollFrame(surrounding);
        if (_preferSelectedContent) _previousSelection = new ManualScrollFrame(initial);
        initial.Tag = new ScrollCapturePosition(_session, 0);
        return true;
    }

    internal Bitmap Capture(ScrollCaptureTarget target)
    {
        using var surrounding = ForegroundWindowCaptureService.CaptureScrollTargetRegion(
            target with { CaptureRegion = _region, Context = null });
        return Extract(surrounding);
    }

    // Shared with deterministic replays: surrounding is one simultaneous
    // screen copy, and no pixels outside the selection enter the result.
    internal Bitmap Extract(Bitmap surrounding)
    {
        var selected = surrounding.Clone(_selection, PixelFormat.Format32bppPArgb);
        ManualScrollFrame? sample = null;
        ManualScrollFrame? selectedSample = null;
        try
        {
            sample = new ManualScrollFrame(surrounding);
            if (_preferSelectedContent) selectedSample = new ManualScrollFrame(selected);
            int? position = null;
            if (_previous is null)
                position = _position;
            else
            {
                const int minimumOverlap = 48;
                // Automatic capture must not make optional surrounding panels
                // authoritative over the selected document. IDE sticky headers
                // and independent tool panes can invalidate the tall strip
                // while the selected code still supplies unique pixel anchors.
                var selectedMatch = _previousSelection is { IsBlank: false } &&
                    selectedSample is { IsBlank: false }
                    ? _previousSelection.Locate(selectedSample, minimumOverlap) : null;
                if (selectedMatch is { } selectedMotion)
                {
                    position = checked(_position + selectedMotion.Shift);
                    // Rebase both references at this measured position. The
                    // surrounding layout may have changed since its last use.
                    _fixedTop = _fixedBottom = 0;
                }
                else
                {
                    if (_fixedTop + _fixedBottom == 0 &&
                        _previous.FindFixedRegions(sample, minimumOverlap) is { } fixedRows)
                    {
                        _fixedTop = fixedRows.Top;
                        _fixedBottom = fixedRows.Bottom;
                    }
                    var match = _previous.Locate(sample, minimumOverlap, _fixedTop, _fixedBottom);
                    if (match is { } located) position = checked(_position + located.Shift);
                }
            }
            if (position is { } measured)
            {
                _position = measured;
                _previous?.Dispose();
                _previous = sample;
                sample = null;
                _previousSelection?.Dispose();
                _previousSelection = selectedSample;
                selectedSample = null;
            }
            selected.Tag = new ScrollCapturePosition(_session, position);
            return selected;
        }
        catch { selected.Dispose(); throw; }
        finally { sample?.Dispose(); selectedSample?.Dispose(); }
    }

    public void Dispose()
    {
        _previous?.Dispose(); _previous = null;
        _previousSelection?.Dispose(); _previousSelection = null;
    }
}
