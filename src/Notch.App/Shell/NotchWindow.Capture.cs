using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Notch.App.Shell;

public partial class NotchWindow
{
    // Extra room around the island so its glow is part of the picture.
    private const double CaptureMargin = 48;
    private const double CaptureScale = 2;

    /// <summary>Lets the notch be opened or closed by a script instead of the pointer (<c>--screenshots=</c>).</summary>
    internal void SetPinned(bool pinned)
    {
        _pinnedOpen = pinned;
        SetExpanded(pinned || _hoverWantsExpanded);
    }

    /// <summary>Waits until the island has stopped growing or shrinking, then a moment longer for fades.</summary>
    internal async Task WaitUntilSettledAsync()
    {
        await Task.Delay(100);
        for (int i = 0; i < 100 && _animator.IsRunning; i++)
        {
            await Task.Delay(50);
        }

        await Task.Delay(400);
    }

    /// <summary>Draws the island and its glow exactly as shown on screen and saves it as a transparent PNG.</summary>
    internal void SavePicture(string path)
    {
        UpdateLayout();
        Rect island = Island.TransformToAncestor(Root).TransformBounds(new Rect(0, 0, Island.ActualWidth, Island.ActualHeight));
        island.Inflate(CaptureMargin, CaptureMargin);
        island.Intersect(new Rect(0, 0, Root.ActualWidth, Root.ActualHeight));

        var full = new RenderTargetBitmap(
            (int)Math.Ceiling(Root.ActualWidth * CaptureScale),
            (int)Math.Ceiling(Root.ActualHeight * CaptureScale),
            96 * CaptureScale,
            96 * CaptureScale,
            PixelFormats.Pbgra32);
        full.Render(Root);

        var crop = new CroppedBitmap(full, new Int32Rect(
            (int)Math.Floor(island.X * CaptureScale),
            (int)Math.Floor(island.Y * CaptureScale),
            (int)Math.Ceiling(island.Width * CaptureScale),
            (int)Math.Ceiling(island.Height * CaptureScale)));

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(crop));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using FileStream file = File.Create(path);
        encoder.Save(file);
    }
}
