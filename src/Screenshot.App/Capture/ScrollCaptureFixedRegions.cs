using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace Screenshot.App.Capture;

/// <summary>Owns one copy of the fixed bars; only the body enters motion matching.</summary>
internal sealed class ScrollCaptureFixedRegions : IDisposable
{
    private readonly Bitmap? _top;
    private readonly Bitmap? _bottom;
    private readonly int _width;
    private readonly int _frameHeight;

    internal int TopRows { get; }
    internal int BottomRows { get; }
    internal int TotalRows => TopRows + BottomRows;

    internal ScrollCaptureFixedRegions(Bitmap initial, int topRows, int bottomRows)
    {
        _width = initial.Width;
        _frameHeight = initial.Height;
        TopRows = Math.Max(0, topRows);
        BottomRows = Math.Max(0, bottomRows);
        if (topRows > 0)
            _top = initial.Clone(new Rectangle(0, 0, _width, topRows), PixelFormat.Format32bppPArgb);
        try
        {
            if (bottomRows > 0)
                _bottom = initial.Clone(new Rectangle(0, initial.Height - bottomRows, _width, bottomRows),
                    PixelFormat.Format32bppPArgb);
        }
        catch { _top?.Dispose(); throw; }
    }

    internal Bitmap CropContent(Bitmap frame)
    {
        if (frame.Width != _width || frame.Height != _frameHeight)
            throw new ArgumentException("滚动截图帧尺寸发生了变化。", nameof(frame));
        var content = frame.Clone(new Rectangle(0, TopRows, _width, frame.Height - TotalRows),
            PixelFormat.Format32bppPArgb);
        content.Tag = frame.Tag;
        return content;
    }

    // For a live preview, body is the existing narrow incremental strip cache,
    // not a full-resolution composition. Work is bounded by the preview size.
    internal Bitmap Render(Bitmap body, Rectangle source, int contentHeight,
        int maximumWidth, int maximumHeight)
    {
        var totalHeight = checked(contentHeight + TotalRows);
        var scale = Math.Min(1d, Math.Min(maximumWidth / (double)_width, maximumHeight / (double)totalHeight));
        var width = Math.Max(1, (int)Math.Round(_width * scale));
        var height = Math.Max(1, (int)Math.Round(totalHeight * scale));
        var top = (int)Math.Round(TopRows * scale);
        var bottom = (int)Math.Round((TopRows + contentHeight) * scale);
        var result = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
        try
        {
            using var graphics = Graphics.FromImage(result);
            graphics.CompositingMode = CompositingMode.SourceCopy;
            graphics.InterpolationMode = InterpolationMode.HighQualityBilinear;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            using var attributes = new ImageAttributes();
            attributes.SetWrapMode(WrapMode.TileFlipXY);
            if (_top is not null && top > 0)
                Draw(_top, new Rectangle(0, 0, width, top), new Rectangle(0, 0, _width, TopRows));
            if (bottom > top)
                Draw(body, new Rectangle(0, top, width, bottom - top), source);
            if (_bottom is not null && height > bottom)
                Draw(_bottom, new Rectangle(0, bottom, width, height - bottom), new Rectangle(0, 0, _width, BottomRows));
            return result;

            void Draw(Bitmap image, Rectangle target, Rectangle region)
            {
                // GDI+ bilinear filtering can round edge pixels even at 1:1.
                // The exported image must preserve the original pixels exactly.
                var unscaled = target.Size == region.Size;
                if (unscaled)
                {
                    var state = graphics.Save();
                    graphics.SetClip(target);
                    graphics.DrawImageUnscaled(image, target.X - region.X, target.Y - region.Y);
                    graphics.Restore(state);
                    return;
                }
                graphics.DrawImage(image, target, region.X, region.Y, region.Width, region.Height,
                    GraphicsUnit.Pixel, attributes);
            }
        }
        catch { result.Dispose(); throw; }
    }

    public void Dispose()
    {
        _top?.Dispose();
        _bottom?.Dispose();
    }
}
