namespace Notch.Core.Plugins;

/// <summary>The log file all plugins and the plugin manager write to. Failing to write is never an error.</summary>
public sealed class PluginLog(string filePath)
{
    private const long MaxBytes = 1024 * 1024;

    private readonly Lock _gate = new();

    public string FilePath { get; } = filePath;

    /// <param name="source">A plugin id, or "notch" for the plugin manager itself.</param>
    public void Write(string source, string level, string message, Exception? exception = null)
    {
        string entry = $"{DateTimeOffset.Now:u} [{level}] {source}: {message}{Environment.NewLine}";
        if (exception is not null)
        {
            entry += exception + Environment.NewLine;
        }

        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);

                // Keep one previous file so a chatty plugin cannot fill the disk.
                var file = new FileInfo(FilePath);
                if (file.Exists && file.Length > MaxBytes)
                {
                    File.Move(FilePath, FilePath + ".old", overwrite: true);
                }

                File.AppendAllText(FilePath, entry);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Nowhere left to report to.
            }
        }
    }
}
