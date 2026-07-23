using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Screenshot.App.Capture;

/// <summary>
/// Rejects captures taken before the target has painted a new viewport. Screen
/// capture can run faster than DWM refresh; queueing those identical bitmaps
/// wastes the matcher budget and eventually discards the overlap bridge.
/// </summary>
internal sealed class CapturedFrameGate
{
    private const int SignatureWidth = 48;
    private const int SignatureHeight = 32;
    private const double MaximumAverageDifference = 0.006;
    private const double MaximumChangedSampleRatio = 0.035;
    private byte[]? _acceptedSignature;
    private byte[]? _pendingSignature;

    public bool HasChanged(Bitmap frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        _pendingSignature = CreateSignature(frame);

        if (_acceptedSignature is null)
        {
            return true;
        }

        long totalDifference = 0;
        var changedSamples = 0;
        var sampleCount = _pendingSignature.Length / 3;

        for (var index = 0; index < _pendingSignature.Length; index += 3)
        {
            var difference =
                Math.Abs(_acceptedSignature[index] - _pendingSignature[index]) +
                Math.Abs(_acceptedSignature[index + 1] - _pendingSignature[index + 1]) +
                Math.Abs(_acceptedSignature[index + 2] - _pendingSignature[index + 2]);
            totalDifference += difference;
            if (difference > 30)
            {
                changedSamples++;
            }
        }

        var averageDifference = totalDifference / (sampleCount * 255d * 3d);
        var changedRatio = changedSamples / (double)sampleCount;
        return averageDifference > MaximumAverageDifference ||
               changedRatio > MaximumChangedSampleRatio;
    }

    public void AcceptPending()
    {
        if (_pendingSignature is not null)
        {
            _acceptedSignature = _pendingSignature;
            _pendingSignature = null;
        }
    }

    public void Accept(Bitmap frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        _acceptedSignature = CreateSignature(frame);
        _pendingSignature = null;
    }

    private static byte[] CreateSignature(Bitmap frame)
    {
        using var reduced = new Bitmap(
            SignatureWidth,
            SignatureHeight,
            PixelFormat.Format32bppPArgb);
        using (var graphics = Graphics.FromImage(reduced))
        {
            graphics.CompositingMode = CompositingMode.SourceCopy;
            graphics.InterpolationMode = InterpolationMode.Low;
            graphics.PixelOffsetMode = PixelOffsetMode.HighSpeed;
            graphics.DrawImage(
                frame,
                new Rectangle(0, 0, SignatureWidth, SignatureHeight),
                0,
                0,
                frame.Width,
                frame.Height,
                GraphicsUnit.Pixel);
        }

        var data = reduced.LockBits(
            new Rectangle(0, 0, SignatureWidth, SignatureHeight),
            ImageLockMode.ReadOnly,
            PixelFormat.Format32bppPArgb);
        try
        {
            var raw = new byte[Math.Abs(data.Stride) * SignatureHeight];
            Marshal.Copy(data.Scan0, raw, 0, raw.Length);
            var signature = new byte[SignatureWidth * SignatureHeight * 3];
            var destination = 0;

            for (var y = 0; y < SignatureHeight; y++)
            {
                var row = y * Math.Abs(data.Stride);
                for (var x = 0; x < SignatureWidth; x++)
                {
                    var source = row + (x * 4);
                    signature[destination++] = raw[source];
                    signature[destination++] = raw[source + 1];
                    signature[destination++] = raw[source + 2];
                }
            }

            return signature;
        }
        finally
        {
            reduced.UnlockBits(data);
        }
    }
}
