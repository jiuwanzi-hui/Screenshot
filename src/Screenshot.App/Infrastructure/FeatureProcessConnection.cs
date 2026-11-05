using System.Buffers.Binary;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Screenshot.App.Capture;
using Screenshot.App.Core;

namespace Screenshot.App.Infrastructure;

// Only used over a randomly named, current-user-only local pipe. Images travel
// as rows of original BGRA pixels, without PNG/base64 or a retained large pool.
internal sealed record FeatureMessage(string Kind)
{
    public long Sequence { get; init; }
    public string? Command { get; init; }
    public HotKeyAction? HotKey { get; init; }
    public long TargetWindow { get; init; }
    public AppSettings? Settings { get; init; }
    public string? Text { get; init; }
    public bool Flag { get; init; }
    public int Count { get; init; }
    public int Section { get; init; }
    public CapturePointerButton? Button { get; init; }
    public int? PointerX { get; init; }
    public int? PointerY { get; init; }
    public bool EnterPickerOnRelease { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public ScreenRegion? Region { get; init; }
    public ScreenRegion? LastCaptureRegion { get; init; }
    public ScreenRegion? InitialSelection { get; init; }
    public long NativeSelectionId { get; init; }
    public long NativeSelectionWindow { get; init; }
    public bool OpaquePixels { get; init; }
    [JsonIgnore] public CapturedImage? Image { get; set; }
    [JsonIgnore] public NativeDesktopFrame? NativeFrame { get; init; }

    public static FeatureMessage ForHotKey(HotKeyPressedEventArgs args) => new("command")
    {
        HotKey = args.Action,
        Image = args.DetachPreCapturedScreen(),
        Button = args.CapturePointerContinuation?.Button,
        PointerX = args.CapturePointerContinuation?.StartScreenPoint?.X,
        PointerY = args.CapturePointerContinuation?.StartScreenPoint?.Y,
        EnterPickerOnRelease = args.CapturePointerContinuation?.EnterPickerWhenReleasedWithoutSelection == true,
    };
}

internal sealed class FeatureProcessConnection(Stream stream) : IDisposable
{
    // Per-transfer scratch only. Batching keeps a 4K frame from scheduling
    // thousands of pipe operations on the path to the first toolbar frame.
    private const int PixelBlockBytes = 256 * 1024;
    private readonly SemaphoreSlim _writer = new(1, 1);
    private readonly CancellationTokenSource _closed = new();
    private bool _disposed;

    public async Task SendAsync(FeatureMessage message)
    {
        CaptureTimingDiagnostics.Mark("pipe-send-start", $"kind={message.Kind} selection={message.NativeSelectionId} sequence={message.Sequence}");
        var token = _closed.Token;
        await _writer.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var image = message.Image;
            var native = message.NativeFrame;
            var header = JsonSerializer.SerializeToUtf8Bytes(message with
            {
                Width = image?.PixelWidth ?? native?.Region.Width ?? 0,
                Height = image?.PixelHeight ?? native?.Region.Height ?? 0,
                Region = image?.SourceRegion ?? native?.Region,
                OpaquePixels = native is not null || message.OpaquePixels,
            }, FeatureJsonContext.Default.FeatureMessage);
            var length = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(length, header.Length);
            await stream.WriteAsync(length, token).ConfigureAwait(false);
            await stream.WriteAsync(header, token).ConfigureAwait(false);
            if (native is not null)
            {
                await WritePixelsAsync(native.Pixels, native.Stride, native.Stride,
                    native.Region.Height, token).ConfigureAwait(false);
            }
            else if (image is not null)
            {
                var bitmap = image.Bitmap;
                var data = bitmap.LockBits(new Rectangle(0, 0, image.PixelWidth, image.PixelHeight),
                    ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                try
                {
                    await WritePixelsAsync(data.Scan0, data.Stride, checked(image.PixelWidth * 4),
                        image.PixelHeight, token).ConfigureAwait(false);
                }
                finally { bitmap.UnlockBits(data); }
            }
            await stream.FlushAsync(token).ConfigureAwait(false);
            CaptureTimingDiagnostics.Mark("pipe-send-end", $"kind={message.Kind} selection={message.NativeSelectionId} sequence={message.Sequence}");
        }
        finally { _writer.Release(); }
    }

    public async Task<FeatureMessage> ReadAsync()
    {
        var token = _closed.Token;
        var length = new byte[4];
        await stream.ReadExactlyAsync(length, token).ConfigureAwait(false);
        var count = BinaryPrimitives.ReadInt32LittleEndian(length);
        if (count is <= 0 or > 1_048_576) throw new InvalidDataException("Invalid feature message size.");
        var header = new byte[count];
        await stream.ReadExactlyAsync(header, token).ConfigureAwait(false);
        var message = JsonSerializer.Deserialize(header, FeatureJsonContext.Default.FeatureMessage)
            ?? throw new InvalidDataException("Missing feature message.");
        if (message.Width == 0 && message.Height == 0) return message;
        CaptureTimingDiagnostics.Mark("pipe-frame-read-start", $"kind={message.Kind} selection={message.NativeSelectionId} width={message.Width} height={message.Height}");
        if (message.Width is <= 0 or > 65536 || message.Height is <= 0 or > 65536 ||
            (long)message.Width * message.Height > 134_217_728)
            throw new InvalidDataException("Invalid screenshot dimensions.");
        var pixelFormat = message.OpaquePixels ? PixelFormat.Format32bppRgb : PixelFormat.Format32bppArgb;
        var bitmap = new Bitmap(message.Width, message.Height, pixelFormat);
        try
        {
            var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height),
                ImageLockMode.WriteOnly, pixelFormat);
            try
            {
                var rowBytes = checked(message.Width * 4);
                var rowsPerBlock = Math.Min(message.Height, Math.Max(1, PixelBlockBytes / rowBytes));
                var block = new byte[checked(rowBytes * rowsPerBlock)];
                for (var y = 0; y < message.Height; y += rowsPerBlock)
                {
                    var rows = Math.Min(rowsPerBlock, message.Height - y);
                    var bytes = checked(rows * rowBytes);
                    await stream.ReadExactlyAsync(block.AsMemory(0, bytes), token).ConfigureAwait(false);
                    if (data.Stride == rowBytes)
                        Marshal.Copy(block, 0, data.Scan0 + checked(y * data.Stride), bytes);
                    else
                        for (var row = 0; row < rows; row++)
                            Marshal.Copy(block, row * rowBytes, data.Scan0 + checked((y + row) * data.Stride), rowBytes);
                }
            }
            finally { bitmap.UnlockBits(data); }
            message.Image = new CapturedImage(bitmap, message.Region);
            CaptureTimingDiagnostics.Mark("pipe-frame-read-end", $"selection={message.NativeSelectionId}");
            if (message.Kind == "prepare-capture-frame" ||
                message.Kind == "command" && message.NativeSelectionId != 0 && message.HotKey == HotKeyAction.RegionCapture)
            {
                CaptureTimingDiagnostics.Mark("pipe-frame-preview-start", $"selection={message.NativeSelectionId}");
                _ = await Task.Run(message.Image.WarmPreview).ConfigureAwait(false);
                CaptureTimingDiagnostics.Mark("pipe-frame-preview-end", $"selection={message.NativeSelectionId}");
            }
            return message;
        }
        catch { bitmap.Dispose(); throw; }
    }

    private async Task WritePixelsAsync(IntPtr pixels, int stride, int rowBytes, int height, CancellationToken token)
    {
        var rowsPerBlock = Math.Min(height, Math.Max(1, PixelBlockBytes / rowBytes));
        var block = new byte[checked(rowBytes * rowsPerBlock)];
        for (var y = 0; y < height; y += rowsPerBlock)
        {
            token.ThrowIfCancellationRequested();
            var rows = Math.Min(rowsPerBlock, height - y);
            var bytes = checked(rows * rowBytes);
            if (stride == rowBytes)
                Marshal.Copy(pixels + checked(y * stride), block, 0, bytes);
            else
                for (var row = 0; row < rows; row++)
                    Marshal.Copy(pixels + checked((y + row) * stride), block, row * rowBytes, rowBytes);
            await stream.WriteAsync(block.AsMemory(0, bytes), token).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _closed.Cancel();
        stream.Dispose();
        // Outstanding readers/writers observe cancellation before releasing
        // their gate. Do not dispose a semaphore while a waiter is unwinding.
    }
}

// Avoid discovering the entire settings graph with reflection on each worker
// startup. The wire representation remains the existing numeric-enum JSON.
[JsonSerializable(typeof(FeatureMessage))]
internal partial class FeatureJsonContext : JsonSerializerContext;
