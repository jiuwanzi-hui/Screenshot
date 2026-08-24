namespace SnapCut.Core;

/// <summary>
/// Rejects captures taken before the target has painted a new viewport. Screen
/// capture can run faster than the compositor refresh; queueing those identical
/// frames wastes the matcher budget and eventually discards the overlap bridge.
/// </summary>
public sealed class CapturedFrameGate
{
    private const int SignatureWidth = 48;
    private const int SignatureHeight = 32;
    private const double MaximumAverageDifference = 0.006;
    private const double MaximumChangedSampleRatio = 0.035;
    private byte[]? _acceptedSignature;
    private byte[]? _pendingSignature;

    public bool HasChanged(PixelImage frame)
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

    public void Accept(PixelImage frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        _acceptedSignature = CreateSignature(frame);
        _pendingSignature = null;
    }

    private static byte[] CreateSignature(PixelImage frame)
    {
        var reduced = frame.DownscaleTo(SignatureWidth, SignatureHeight);
        var signature = new byte[SignatureWidth * SignatureHeight * 3];
        var destination = 0;

        for (var y = 0; y < SignatureHeight; y++)
        {
            var row = y * reduced.Stride;
            for (var x = 0; x < SignatureWidth; x++)
            {
                var source = row + (x * 4);
                signature[destination++] = reduced.Pixels[source];
                signature[destination++] = reduced.Pixels[source + 1];
                signature[destination++] = reduced.Pixels[source + 2];
            }
        }

        return signature;
    }
}
