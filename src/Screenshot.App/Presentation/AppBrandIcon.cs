using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Screenshot.App.Presentation;

/// <summary>Use the full-resolution ICO frame for client-drawn branding at every DPI.</summary>
public static class AppBrandIcon
{
    public static ImageSource Source { get; } = Load();

    private static BitmapFrame Load()
    {
        var decoder = BitmapDecoder.Create(
            new Uri("pack://application:,,,/SnapCut;component/Assets/Screenshot.ico"),
            BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames.MaxBy(image => image.PixelWidth * image.PixelHeight)!;
        frame.Freeze();
        return frame;
    }
}
