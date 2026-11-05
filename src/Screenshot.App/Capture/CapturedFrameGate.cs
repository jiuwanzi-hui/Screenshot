using System.Buffers;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Screenshot.App.Capture;

/// <summary>Drop only identical captures; sparse new rows must reach the stitcher.</summary>
internal sealed class CapturedFrameGate : IDisposable
{
    private byte[]? _acceptedPixels;
    private byte[]? _pendingPixels;
    private Size _acceptedSize;
    private Size _pendingSize;
    private ScrollCapturePosition? _acceptedPosition;
    private ScrollCapturePosition? _pendingPosition;

    public bool HasChanged(Bitmap frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var length = checked(frame.Width * frame.Height * 4);
        if (_pendingPixels is null || _pendingPixels.Length < length)
        {
            if (_pendingPixels is not null) ArrayPool<byte>.Shared.Return(_pendingPixels);
            _pendingPixels = null;
            _pendingPixels = ArrayPool<byte>.Shared.Rent(length);
        }
        var data = frame.LockBits(new Rectangle(Point.Empty, frame.Size),
            ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        try
        {
            for (var y = 0; y < frame.Height; y++)
                Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), _pendingPixels,
                    y * frame.Width * 4, frame.Width * 4);
        }
        finally { frame.UnlockBits(data); }
        _pendingSize = frame.Size;
        _pendingPosition = ScrollCapturePosition.Read(frame);
        return _acceptedPosition != _pendingPosition ||
            _acceptedPixels is null || _acceptedSize != _pendingSize ||
            !_pendingPixels.AsSpan(0, length).SequenceEqual(_acceptedPixels.AsSpan(0, length));
    }

    public void AcceptPending()
    {
        if (_pendingSize.IsEmpty) return;
        (_acceptedPixels, _pendingPixels) = (_pendingPixels, _acceptedPixels);
        _acceptedSize = _pendingSize;
        _acceptedPosition = _pendingPosition;
        _pendingSize = Size.Empty;
    }

    internal bool ShouldRetainBlankMotion(bool hasPendingInput, int fixedTopRows, int fixedBottomRows)
    {
        if (!hasPendingInput || _pendingPixels is null || _pendingSize.IsEmpty) return false;
        var top = Math.Clamp(fixedTopRows, 0, _pendingSize.Height);
        var bottom = Math.Clamp(_pendingSize.Height - fixedBottomRows, top, _pendingSize.Height);
        if (bottom <= top) return false;
        var width = _pendingSize.Width;
        var right = width - (width >= 80 ? Math.Clamp(width / 80, 10, 24) : 0);
        var reference = top * width * 4;
        for (var y = top; y < bottom; y++)
        for (var x = 0; x < right; x++)
        {
            var p = (y * width + x) * 4;
            var difference = Math.Abs(_pendingPixels[p] - _pendingPixels[reference]) +
                Math.Abs(_pendingPixels[p + 1] - _pendingPixels[reference + 1]) +
                Math.Abs(_pendingPixels[p + 2] - _pendingPixels[reference + 2]);
            if (difference > 2) return false;
        }
        return true;
    }

    public void Accept(Bitmap frame)
    {
        HasChanged(frame);
        AcceptPending();
    }

    public void Dispose()
    {
        if (_acceptedPixels is not null) ArrayPool<byte>.Shared.Return(_acceptedPixels);
        if (_pendingPixels is not null) ArrayPool<byte>.Shared.Return(_pendingPixels);
        _acceptedPixels = _pendingPixels = null;
        _acceptedSize = _pendingSize = Size.Empty;
    }
}
