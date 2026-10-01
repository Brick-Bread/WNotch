using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

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
