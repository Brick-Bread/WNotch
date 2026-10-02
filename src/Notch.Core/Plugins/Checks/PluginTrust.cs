using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Notch.Core.Plugins.Checks;

/// <summary>
/// Remembers which files of each plugin the user approved. A plugin whose files changed since
/// (swapped DLL, edited manifest) is not started until the user approves it again, so a plugin
/// cannot turn into something else after it was accepted.
/// </summary>
public sealed class PluginTrustStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private readonly Lock _gate = new();
    private readonly string _path;
    private Dictionary<string, Record>? _records;

    public PluginTrustStore(string path) => _path = path;

    /// <summary>A short hash of every file in the folder, names included, so any change shows.</summary>
    public static string TreeHash(string directory)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        if (Directory.Exists(directory))
        {
            string[] files = [.. Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(directory, f))
                .Order(StringComparer.OrdinalIgnoreCase)];
            foreach (string relative in files)
            {
                hash.AppendData(Encoding.UTF8.GetBytes(relative.Replace('\\', '/').ToLowerInvariant()));
                hash.AppendData([0]);
                using FileStream stream = File.OpenRead(Path.Combine(directory, relative));
                var buffer = new byte[81920];
                int read;
                while ((read = stream.Read(buffer)) > 0)
                {
                    hash.AppendData(buffer.AsSpan(0, read));
                }

                hash.AppendData([0]);
            }
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    /// <summary>True when the user approved exactly these files for this plugin.</summary>
    public bool IsApproved(string pluginId, string treeHash)
    {
        lock (_gate)
        {
            return Load().TryGetValue(pluginId, out Record? record) && record.Hash == treeHash;
        }
    }

    /// <summary>True when the plugin has been approved at some point, in any version.</summary>
    public bool HasRecord(string pluginId)
    {
        lock (_gate)
        {
            return Load().ContainsKey(pluginId);
        }
    }

    public void Approve(string pluginId, string treeHash)
    {
        lock (_gate)
        {
            Load()[pluginId] = new Record(treeHash, DateTimeOffset.UtcNow);
            Save();
        }
    }

    public void Forget(string pluginId)
    {
        lock (_gate)
        {
            if (Load().Remove(pluginId))
            {
                Save();
            }
        }
    }

    private Dictionary<string, Record> Load()
    {
        if (_records is not null)
        {
            return _records;
        }

        try
        {
            if (File.Exists(_path))
            {
                _records = JsonSerializer.Deserialize<Dictionary<string, Record>>(File.ReadAllText(_path), Options);
            }
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            // An unreadable file means nothing is approved, which is the safe reading.
        }

        return _records ??= [];
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            string temporary = _path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(_records, Options));
            File.Move(temporary, _path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Approval then lasts for this run only.
        }
    }

    private sealed record Record(string Hash, DateTimeOffset Approved);
}

/// <summary>A plugin (or one version of it) the registry says must not run.</summary>
/// <param name="Sha256">A tree hash; null revokes every version of the plugin.</param>
public sealed record Revocation(string Id, string? Sha256, string Reason);

/// <summary>
/// The registry's list of plugins withdrawn for being harmful. It can only ever stop a plugin, so
/// a forged or stale list costs the user a plugin and nothing worse.
/// </summary>
public static class PluginRevocations
{
    private const int MaxEntries = 1000;

    /// <summary>Reads the <c>revoked</c> array of a registry file; anything unreadable gives an empty list.</summary>
    public static IReadOnlyList<Revocation> Read(string registryPath)
    {
        try
        {
            return File.Exists(registryPath) ? Parse(File.ReadAllText(registryPath)) : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public static IReadOnlyList<Revocation> Parse(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true });
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("revoked", out JsonElement list)
                || list.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            List<Revocation> result = [];
            foreach (JsonElement item in list.EnumerateArray().Take(MaxEntries))
            {
                if (item.ValueKind == JsonValueKind.Object
                    && item.TryGetProperty("id", out JsonElement id) && id.ValueKind == JsonValueKind.String
                    && id.GetString() is { Length: > 0 and <= 64 } pluginId)
                {
                    string? hash = item.TryGetProperty("sha256", out JsonElement h) && h.ValueKind == JsonValueKind.String
                        ? h.GetString()?.ToLowerInvariant()
                        : null;
                    string reason = item.TryGetProperty("reason", out JsonElement r) && r.ValueKind == JsonValueKind.String
                        ? r.GetString() ?? ""
                        : "";
                    result.Add(new Revocation(pluginId, string.IsNullOrWhiteSpace(hash) ? null : hash, reason.Length > 200 ? reason[..200] : reason));
                }
            }

            return result;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>The revocation that applies to this plugin and file set, or null.</summary>
    public static Revocation? Find(IReadOnlyList<Revocation> revocations, string pluginId, string treeHash) =>
        revocations.FirstOrDefault(r => r.Id == pluginId && (r.Sha256 is null || r.Sha256 == treeHash));
}
