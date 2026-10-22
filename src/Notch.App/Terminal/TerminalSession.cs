using System.ComponentModel;
using System.IO;
using System.Windows.Media;
using Notch.Core.Agents;
using Notch.Core.Terminal;
using Notch.Platform.Terminal;

namespace Notch.App.Terminal;

/// <summary>One tab in the built-in terminal: a profile running in a folder.</summary>
internal sealed class TerminalSession : INotifyPropertyChanged
{
    private bool _isActive;

    public TerminalSession(TerminalProfile profile, string folder)
    {
        Profile = profile;
        Folder = folder;
        Agent = new AgentStatusTracker(profile.Agent);
        Agent.Changed += () => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusBrush)));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Id { get; } = Guid.NewGuid().ToString("N");

    public TerminalProfile Profile { get; }

    public string Folder { get; }

    public AgentStatusTracker Agent { get; }

    public PseudoConsoleSession? Process { get; set; }

    /// <summary>Last time the process printed or the user submitted a line; used to spot a stalled "working" state.</summary>
    public DateTime LastActivity { get; set; } = DateTime.UtcNow;

    public string Glyph => Profile.Glyph;

    /// <summary>The agent's logo for the session's tab, or null for a shell or an agent Notch has no logo for.</summary>
    public Geometry? Logo => AgentCatalog.ForKind(Profile.Agent) is { } agent
        ? System.Windows.Application.Current.TryFindResource("AgentLogo." + agent.Id) as Geometry
        : null;

    public string Title => $"{Profile.DisplayName} · {FolderName}";

    public string FolderName => Path.GetFileName(Folder.TrimEnd('\\', '/')) is { Length: > 0 } name ? name : Folder;

    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (_isActive != value)
            {
                _isActive = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsActive)));
            }
        }
    }

    /// <summary>Colour of the status dot on the session's tab; transparent when there is nothing to report.</summary>
    public Brush StatusBrush => Agent.State switch
    {
        AgentState.Working => Themed("WorkingBrush"),
        AgentState.NeedsInput => Themed("AttentionBrush"),
        AgentState.Done => Themed("DoneBrush"),
        _ => Brushes.Transparent,
    };

    private static Brush Themed(string key) =>
        System.Windows.Application.Current.TryFindResource(key) as Brush ?? Brushes.Transparent;
}
