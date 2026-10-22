using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Notch.App.Terminal;
using Notch.Core.Agents;

namespace Notch.App.Shell;

// The Agents card on the Home tab: every coding agent there is, wherever it runs.
public partial class NotchWindow
{
    /// <summary>The most agents the card lists; the rest are counted.</summary>
    private const int MaxAgentRows = 4;

    private readonly AgentBoard _agents;
    private readonly Func<AgentEntry, bool> _focusDetectedAgent;
    private int _agentRowsShown;

    private void InitializeAgents()
    {
        _agents.Changed += () => Dispatcher.BeginInvoke(UpdateAgentCard);
        UpdateAgentCard();
    }

    private void UpdateAgentCard()
    {
        IReadOnlyList<AgentEntry> all = _agents.Snapshot();

        // The ones that want attention come first, then the busy ones.
        AgentRow[] rows = [.. all
            .OrderBy(entry => entry.State switch
            {
                AgentState.NeedsInput => 0,
                AgentState.Done => 1,
                AgentState.Working => 2,
                _ => 3,
            })
            .Take(MaxAgentRows)
            .Select(entry => new AgentRow(entry))];

        AgentList.ItemsSource = rows;

        if (_agentRowsShown != rows.Length)
        {
            // The Home tab is taller with more rows, and the widget may be hidden with none.
            _agentRowsShown = rows.Length;
            HomeContentChanged();
        }
    }

    private void OnAgentRowClicked(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { Tag: AgentRow row })
        {
            e.Handled = true;
            JumpToAgent(row.Entry);
        }
    }

    /// <summary>Takes the user to the agent: its terminal tab here, or the window it runs in.</summary>
    private void JumpToAgent(AgentEntry entry)
    {
        if (entry.Source == AgentSource.Notch)
        {
            if (_terminal.Sessions.FirstOrDefault(s => s.Id == entry.Key) is { } session)
            {
                TabTerminal.IsChecked = true;
                _terminal.Activate(session);
            }

            return;
        }

        if (!_focusDetectedAgent(entry))
        {
            // Nothing to raise (a console that has no window of its own, or the program has exited).
            _activities.Publish(new Notch.Core.Activities.Activity
            {
                Id = "notice.agent-jump",
                Tier = Notch.Core.Activities.ActivityTier.Transient,
                Title = "Could not find that window",
                Detail = entry.Host is null ? entry.DisplayName : $"{entry.DisplayName} in {entry.Host}",
                Lifetime = TimeSpan.FromSeconds(3),
            });
        }
    }

    /// <summary>One row of the Agents card.</summary>
    private sealed class AgentRow(AgentEntry entry)
    {
        public AgentEntry Entry { get; } = entry;

        public string Title => string.IsNullOrEmpty(Entry.FolderName) ? Entry.DisplayName : $"{Entry.DisplayName} · {Entry.FolderName}";

        /// <summary>Where it runs: the program a found agent is in, or Notch's terminal.</summary>
        public string Where => Entry.Source == AgentSource.Notch ? "Notch terminal" : Entry.Host ?? "";

        public string StateText => Entry.StateText;

        public string Tip => Entry.Source == AgentSource.Notch ? "Show this session" : "Bring its window to the front";

        public Brush Brush => (Brush)Application.Current.FindResource(Entry.State switch
        {
            AgentState.Working => "WorkingBrush",
            AgentState.NeedsInput => "AttentionBrush",
            AgentState.Done => "DoneBrush",
            _ => "SecondaryTextBrush",
        });

        /// <summary>The agent's logo, or null for one Notch has no logo for (its glyph shows instead).</summary>
        public Geometry? Logo => Application.Current.TryFindResource("AgentLogo." + Entry.AgentId) as Geometry;

        public string? Glyph => Logo is null ? Entry.Glyph : null;
    }
}
