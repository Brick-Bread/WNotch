using System.Text.Json;

namespace Notch.Core.Shelf;

/// <summary>Loads and saves what is on the <see cref="FileShelf"/> as a JSON list of paths. A missing or corrupt file yields an empty shelf.</summary>
public sealed class ShelfStore(string filePath)
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public string FilePath { get; } = filePath;

    public IReadOnlyList<string> Load()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<List<string>>(File.ReadAllText(FilePath), Options) ?? []
                : [];
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public void Save(IReadOnlyList<string> paths)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);

            // Write to a temporary file first so a crash cannot leave a half-written shelf.
            string temporary = FilePath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(paths, Options));
            File.Move(temporary, FilePath, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The shelf is a convenience; failing to persist it must not take the app down.
        }
    }
}
