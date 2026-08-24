using System.Globalization;
using System.IO;
using System.Drawing.Imaging;
using System.Windows.Media.Imaging;

namespace Screenshot.App.Capture;

public static class CaptureFileService
{
    public static string SaveAsPng(CapturedImage capturedImage, string saveDirectory)
    {
        ArgumentNullException.ThrowIfNull(capturedImage);

        return SaveWithUniqueName(
            saveDirectory,
            stream => capturedImage.Bitmap.Save(stream, ImageFormat.Png));
    }

    public static string SaveAsPng(BitmapSource bitmapSource, string saveDirectory)
    {
        ArgumentNullException.ThrowIfNull(bitmapSource);

        return SaveWithUniqueName(
            saveDirectory,
            stream =>
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmapSource));
                encoder.Save(stream);
            });
    }

    private static string SaveWithUniqueName(string saveDirectory, Action<Stream> writeImage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(saveDirectory);
        ArgumentNullException.ThrowIfNull(writeImage);

        var fullSaveDirectory = Path.GetFullPath(saveDirectory);
        Directory.CreateDirectory(fullSaveDirectory);
        var timestamp = DateTime.Now.ToString(
            "yyyyMMdd-HHmmss-fff",
            CultureInfo.InvariantCulture);

        for (var sequence = 0; sequence < 1000; sequence++)
        {
            var suffix = sequence == 0 ? string.Empty : $"-{sequence}";
            var path = Path.Combine(fullSaveDirectory, $"Screenshot-{timestamp}{suffix}.png");

            try
            {
                using var stream = new FileStream(
                    path,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None);
                writeImage(stream);
                return path;
            }
            catch (IOException) when (File.Exists(path))
            {
            }
        }

        throw new IOException("无法生成不重复的截图文件名。");
    }
}
