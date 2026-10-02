using System.Text.Json;

namespace Notch.Core.Plugins.Checks;

/// <summary>What happened to a plugin that the user may later want to look up: starts, blocked actions, kills.</summary>
public interface IPluginAudit
{
    void Record(string pluginId, string kind, string detail);
}

public sealed class NullPluginAudit : IPluginAudit
{
    public static readonly NullPluginAudit Instance = new();

    public void Record(string pluginId, string kind, string detail)
    {
    }
}

/// <summary>
/// Appends one JSON object per line to a file (<c>plugin-audit.log</c>). Never throws: auditing
/// must not be a way to crash Notch or a plugin. Keeps the file under about 1 MB by rotating it once.
/// </summary>
public sealed class FilePluginAudit(string path, TimeProvider? time = null) : IPluginAudit
{
    private const long MaxBytes = 1024 * 1024;

    private readonly Lock _gate = new();
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public string Path { get; } = path;

    public void Record(string pluginId, string kind, string detail)
    {
        string line = JsonSerializer.Serialize(new
        {
            time = _time.GetUtcNow().ToString("O"),
            plugin = pluginId,
            kind,
            detail = detail.Length > 500 ? detail[..500] : detail,
        });

        lock (_gate)
        {
            try
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                if (File.Exists(Path) && new FileInfo(Path).Length > MaxBytes)
                {
                    File.Move(Path, Path + ".1", overwrite: true);
                }

                File.AppendAllText(Path, line + Environment.NewLine);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // The record is lost; the plugin carries on.
            }
        }
    }
}

/// <summary>Audit kinds, so readers of the log and tests agree on the words.</summary>
public static class PluginAuditKinds
{
    public const string Started = "started";
    public const string Stopped = "stopped";
    public const string Failed = "failed";
    public const string Denied = "denied";
    public const string Blocked = "blocked";
    public const string Killed = "killed";
    public const string Quarantined = "quarantined";
    public const string Approved = "approved";
    public const string Revoked = "revoked";
    public const string Violation = "violation";
}
