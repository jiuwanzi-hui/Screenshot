using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace Screenshot.App.Capture;

public static class ClipboardFileService
{
    public static Task SetFileAsync(string filePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        var fullPath = Path.GetFullPath(filePath);
        return NativeClipboardWriter.WriteAsync(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(fullPath)) throw new FileNotFoundException("视频文件不存在。", fullPath);
            using var payload = new ClipboardPayload(15, CreateFileDropPayload(fullPath));
            NativeClipboardWriter.Publish(cancellationToken, payload);
        }, cancellationToken);
    }

    internal static byte[] CreateFileDropPayload(string fullPath)
    {
        // DROPFILES: a 20-byte header, fWide=TRUE, and a double-NUL-terminated
        // Unicode path list. No delayed OLE data object remains in the UI process.
        var names = Encoding.Unicode.GetBytes(fullPath + "\0\0");
        var bytes = new byte[checked(20 + names.Length)];
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0, 4), 20);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(16, 4), 1);
        names.CopyTo(bytes, 20);
        return bytes;
    }
}
