using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ZXing;

namespace PsiTun.Services;

/// <summary>
/// Decodes a share-link config from a QR code (as produced by the 3x-ui panel):
/// pick an image file or read the clipboard (plain link text or a bitmap QR).
/// </summary>
public static class QrCodeService
{
    /// <summary>
    /// Clipboard takes priority for a plain share-link/subscription string;
    /// otherwise it decodes a QR code from a bitmap on the clipboard.
    /// </summary>
    public static string? ReadFromClipboard()
    {
        if (Clipboard.ContainsText())
        {
            var text = Clipboard.GetText()?.Trim();
            if (!string.IsNullOrEmpty(text)) return text;
        }

        if (Clipboard.ContainsImage())
            return DecodeImage(Clipboard.GetImage());

        return null;
    }

    public static string? DecodeFile(string path)
    {
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;   // do not keep the file locked
        bmp.UriSource = new Uri(path);
        bmp.EndInit();
        bmp.Freeze();
        return DecodeImage(bmp);
    }

    public static string? DecodeImage(BitmapSource? source)
    {
        if (source is null) return null;

        // Normalize any pixel format to premultiplied BGRA so CopyPixels is unambiguous.
        var fmt = source.Format;
        var bmp = fmt == PixelFormats.Bgra32 || fmt == PixelFormats.Pbgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Pbgra32, null, 0);

        var width = bmp.PixelWidth;
        var height = bmp.PixelHeight;
        if (width <= 0 || height <= 0) return null;

        var stride = width * 4;
        var pixels = new byte[stride * height];
        bmp.CopyPixels(pixels, stride, 0);

        var luminance = new RGBLuminanceSource(pixels, width, height,
            RGBLuminanceSource.BitmapFormat.BGRA32);

        var reader = new BarcodeReaderGeneric
        {
            AutoRotate = true,
            Options =
            {
                TryHarder = true,
                PossibleFormats = new List<BarcodeFormat> { BarcodeFormat.QR_CODE }
            }
        };

        return reader.Decode(luminance)?.Text;
    }
}
