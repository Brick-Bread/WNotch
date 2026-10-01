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
    private static readonly Brush WorkingBrush = Frozen(0x4C, 0xC2, 0xFF);
    private static readonly Brush AttentionBrush = Frozen(0xFF, 0xB9, 0x00);
    private static readonly Brush DoneBrush = Frozen(0x6C, 0xCB, 0x5F);

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
        AgentState.Working => WorkingBrush,
        AgentState.NeedsInput => AttentionBrush,
        AgentState.Done => DoneBrush,
        _ => Brushes.Transparent,
    };

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
