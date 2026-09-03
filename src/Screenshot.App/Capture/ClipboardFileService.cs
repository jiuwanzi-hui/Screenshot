using System.Collections.Specialized;
using System.IO;
using WpfClipboard = System.Windows.Clipboard;

namespace Screenshot.App.Capture;

public static class ClipboardFileService
{
    public static Task SetFileAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        cancellationToken.ThrowIfCancellationRequested();

        var fullPath = Path.GetFullPath(filePath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("视频文件不存在。", fullPath);
        }

        var files = new StringCollection { fullPath };
        WpfClipboard.SetFileDropList(files);
        return Task.CompletedTask;
    }
}
