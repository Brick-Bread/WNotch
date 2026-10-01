using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Notch.Core.Activities;

namespace Notch.App.Shell;

internal static class ImageLoader
{
    /// <summary>Decodes encoded image bytes at roughly the size they will be shown. Null if the bytes are not an image.</summary>
    public static ImageSource? Decode(byte[]? bytes, int decodePixelWidth)
    {
        if (bytes is null || bytes.Length == 0)
        {
            return null;
        }

        try
        {
            using var stream = new MemoryStream(bytes);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = decodePixelWidth;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>A glow colour that represents the image, or null for greyscale or unreadable images.</summary>
    public static GlowColor? AccentOf(ImageSource? image)
    {
        if (image is not BitmapSource bitmap)
        {
            return null;
        }

        try
        {
            var pixels32 = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
            int stride = pixels32.PixelWidth * 4;
            byte[] pixels = new byte[stride * pixels32.PixelHeight];
            pixels32.CopyPixels(pixels, stride, 0);
            return AccentColor.FromPixels(pixels);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static Brush? ToCoverBrush(ImageSource? image)
    {
        if (image is null)
        {
            return null;
        }

        var brush = new ImageBrush(image) { Stretch = Stretch.UniformToFill };
        brush.Freeze();
        return brush;
    }
}
