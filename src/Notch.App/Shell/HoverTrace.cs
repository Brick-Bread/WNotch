using System.IO;

namespace Notch.App.Shell;

/// <summary>
/// Records what the pointer and the window did while the notch opened and closed
/// (<c>--trace-hover</c>), to diagnose hover problems on machines where they show.
/// </summary>
internal static class HoverTrace
{
    private static StreamWriter? _writer;

    public static void Enable()
    {
        try
        {
            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Notch");
            Directory.CreateDirectory(folder);
            _writer = new StreamWriter(Path.Combine(folder, "hover.log"), append: false) { AutoFlush = true };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Tracing is a diagnostic aid; the notch runs the same without it.
        }
    }

    public static void Write(string message) =>
        _writer?.WriteLine($"{DateTimeOffset.Now:HH:mm:ss.fff} {message}");
}
