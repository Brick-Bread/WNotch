using System.Text;

namespace Notch.Core.Plugins.Checks;

/// <summary>
/// The checks the manager makes before it starts a plugin, and the record it keeps. Without one
/// the manager starts whatever the user enabled, as tests do.
/// </summary>
public sealed class PluginChecks
{
    public IPluginAudit Audit { get; init; } = NullPluginAudit.Instance;

    /// <summary>The files the user approved. Null skips the approval check (the scan and the revocation list still apply).</summary>
    public PluginTrustStore? Trust { get; init; }

    /// <summary>The plugins and files the registry has withdrawn, read each time a plugin starts.</summary>
    public Func<IReadOnlyList<Revocation>>? Revocations { get; init; }
}

/// <summary>What the user is shown before they switch a plugin on.</summary>
/// <param name="Permissions">What the plugin says it uses.</param>
/// <param name="FilesChanged">The plugin was approved before, but its files are different now.</param>
public sealed record PluginReview(
    PluginInfo Info,
    IReadOnlyList<string> Permissions,
    ScanReport Scan,
    bool Approved,
    bool FilesChanged,
    Revocation? Revoked)
{
    /// <summary>What the user is told before approving: who made it, what it says it uses, and what Notch noticed inside it.</summary>
    public string Describe()
    {
        var text = new StringBuilder();
        text.Append('"').Append(Info.Name).Append('"');
        if (Info.Version is not null)
        {
            text.Append(' ').Append(Info.Version);
        }

        if (Info.Author is not null)
        {
            text.Append(" by ").Append(Info.Author);
        }

        text.AppendLine().AppendLine();
        if (FilesChanged)
        {
            text.AppendLine("Its files changed since you last approved it.").AppendLine();
        }

        if (Permissions.Count == 0)
        {
            text.AppendLine("It does not say what it uses.");
        }
        else
        {
            text.AppendLine("It says it uses:");
            foreach (string permission in Permissions)
            {
                text.Append("  • ").AppendLine(PluginManifest.DescribePermission(permission));
            }
        }

        string[] noticed = [.. Scan.Findings
            .Where(f => f.Severity == ScanSeverity.Warning)
            .Select(f => f.Detail)
            .Distinct()
            .Take(6)];
        if (noticed.Length > 0)
        {
            text.AppendLine().AppendLine("Notch also noticed it can use:");
            foreach (string detail in noticed)
            {
                text.Append("  • ").AppendLine(detail);
            }
        }

        return text.ToString().TrimEnd();
    }
}
