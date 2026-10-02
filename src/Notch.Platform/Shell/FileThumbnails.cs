using Notch.Platform.Interop;

namespace Notch.Platform.Shell;

/// <summary>A picture of a file: 32-bit pixels with premultiplied alpha, blue first, top row first.</summary>
public sealed record FileThumbnail(int Width, int Height, byte[] Pixels);

/// <summary>The picture Explorer shows for a file or folder: a thumbnail where the file has one, its icon otherwise.</summary>
public static unsafe class FileThumbnails
{
    private static readonly Guid ShellItemImageFactory = new("bcc18b79-ba16-442f-80c4-8a59c30c463b");

    /// <summary>
    /// Null when the path leads nowhere or the shell has no picture for it. Call on a thread
    /// that has COM set up, such as the UI thread; a thumbnail that is not cached yet can take a moment.
    /// </summary>
    /// <param name="size">Longest side wanted, in pixels.</param>
    public static FileThumbnail? For(string path, int size)
    {
        Guid interfaceId = ShellItemImageFactory;
        if (NativeMethods.SHCreateItemFromParsingName(path, 0, &interfaceId, out nint factory) < 0 || factory == 0)
        {
            return null;
        }

        nint bitmap = 0;
        try
        {
            // IShellItemImageFactory::GetImage follows the three IUnknown methods.
            void** methods = *(void***)factory;
            var getImage = (delegate* unmanaged[Stdcall]<nint, SIZE, int, nint*, int>)methods[3];
            return getImage(factory, new SIZE { Width = size, Height = size }, 0, &bitmap) >= 0 && bitmap != 0
                ? Read(bitmap)
                : null;
        }
        finally
        {
            if (bitmap != 0)
            {
                NativeMethods.DeleteObject(bitmap);
            }

            ((delegate* unmanaged[Stdcall]<nint, uint>)(*(void***)factory)[2])(factory);
        }
    }

    private static FileThumbnail? Read(nint bitmap)
    {
        BITMAP shape;
        if (NativeMethods.GetObject(bitmap, sizeof(BITMAP), &shape) == 0 || shape.Width <= 0 || shape.Height <= 0)
        {
            return null;
        }

        // A negative height asks for the rows top first.
        var header = new BITMAPINFOHEADER
        {
            Size = (uint)sizeof(BITMAPINFOHEADER),
            Width = shape.Width,
            Height = -shape.Height,
            Planes = 1,
            BitCount = 32,
        };

        byte[] pixels = new byte[shape.Width * shape.Height * 4];
        nint dc = NativeMethods.CreateCompatibleDC(0);
        try
        {
            fixed (byte* bits = pixels)
            {
                if (NativeMethods.GetDIBits(dc, bitmap, 0, (uint)shape.Height, bits, &header, 0) == 0)
                {
                    return null;
                }
            }
        }
        finally
        {
            NativeMethods.DeleteDC(dc);
        }

        // Some thumbnails come without an alpha channel, which reads as fully transparent.
        bool alphaMissing = true;
        for (int i = 3; i < pixels.Length; i += 4)
        {
            if (pixels[i] != 0)
            {
                alphaMissing = false;
                break;
            }
        }

        if (alphaMissing)
        {
            for (int i = 3; i < pixels.Length; i += 4)
            {
                pixels[i] = 255;
            }
        }

        return new FileThumbnail(shape.Width, shape.Height, pixels);
    }
}
