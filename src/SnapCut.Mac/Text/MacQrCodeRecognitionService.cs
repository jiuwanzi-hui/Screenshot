using SnapCut.Core;
using ZXing;
using ZXing.Common;

namespace SnapCut.Mac.Text;

internal static class MacQrCodeRecognitionService
{
    public static string? Recognize(PixelImage image)
    {
        var rgb = new byte[image.Width * image.Height * 3];
        for (int source = 0, target = 0;
             source < image.Pixels.Length;
             source += 4, target += 3)
        {
            rgb[target] = image.Pixels[source + 2];
            rgb[target + 1] = image.Pixels[source + 1];
            rgb[target + 2] = image.Pixels[source];
        }

        var luminance = new RGBLuminanceSource(
            rgb,
            image.Width,
            image.Height,
            RGBLuminanceSource.BitmapFormat.RGB24);
        var reader = new BarcodeReaderGeneric
        {
            AutoRotate = true,
            Options = new DecodingOptions
            {
                TryHarder = true,
                PossibleFormats =
                [
                    BarcodeFormat.QR_CODE,
                    BarcodeFormat.DATA_MATRIX,
                    BarcodeFormat.AZTEC,
                    BarcodeFormat.PDF_417,
                ],
            },
        };
        return reader.Decode(luminance)?.Text;
    }
}
