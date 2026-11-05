using System.IO;
using System.Windows.Media.Imaging;
using Screenshot.App.Core;

namespace Screenshot.App.Watermarks;

internal sealed class WatermarkImageStore(string? directory = null)
{
    private readonly string _directory = directory ?? Path.Combine(AppMetadata.DataDirectoryPath, "Watermarks");

    internal string GetPath(string id)
    {
        if (!WatermarkSettings.IsValidImageId(id)) throw new ArgumentException("无效的水印图片。", nameof(id));
        return Path.Combine(_directory, id + ".png");
    }

    internal async Task<WatermarkImage> ImportAsync(string path, CancellationToken cancellationToken)
    {
        return await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (new FileInfo(path).Length > 20 * 1024 * 1024) throw new InvalidDataException("水印图片不能超过 20 MB。");
            var info = SixLabors.ImageSharp.Image.Identify(path);
            if ((long)info.Width * info.Height > 24_000_000) throw new InvalidDataException("水印图片不能超过 2400 万像素。");
            var bitmap = LoadFile(path);
            cancellationToken.ThrowIfCancellationRequested();
            var asset = new WatermarkImage { Id = Guid.NewGuid().ToString("N"), Name = Path.GetFileName(path) };
            Directory.CreateDirectory(_directory);
            var destination = GetPath(asset.Id);
            try
            {
                using (var output = File.Create(destination))
                {
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    encoder.Save(output);
                }
                cancellationToken.ThrowIfCancellationRequested();
                return asset;
            }
            catch { File.Delete(destination); throw; }
        }, cancellationToken);
    }

    internal Task<BitmapSource?> LoadAsync(string id, bool preview, CancellationToken cancellationToken) => Task.Run<BitmapSource?>(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrEmpty(id)) return null;
        var path = GetPath(id);
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException("水印图片已丢失，请在设置中重新导入。", path);
        if (info.Length > 100 * 1024 * 1024) throw new InvalidDataException("水印图片过大，请重新导入。");
        var dimensions = SixLabors.ImageSharp.Image.Identify(path);
        if ((long)dimensions.Width * dimensions.Height > 24_000_000) throw new InvalidDataException("水印图片尺寸过大。");
        var image = LoadFile(path,
            preview && dimensions.Width >= dimensions.Height ? Math.Min(600, dimensions.Width) : 0,
            preview && dimensions.Height > dimensions.Width ? Math.Min(600, dimensions.Height) : 0);
        cancellationToken.ThrowIfCancellationRequested();
        return image;
    }, cancellationToken);

    internal void Delete(string id) => File.Delete(GetPath(id));

    private static BitmapImage LoadFile(string path, int decodeWidth = 0, int decodeHeight = 0)
    {
        using var stream = File.OpenRead(path);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.DecodePixelWidth = decodeWidth;
        bitmap.DecodePixelHeight = decodeHeight;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }
}
