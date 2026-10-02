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

    private const double AgentRowHeight = 24;

    /// <summary>The card's own height (label, padding) plus the gap under it, without its rows.</summary>
    private const double AgentCardChrome = 64;

    private readonly AgentBoard _agents;
    private readonly Func<AgentEntry, bool> _focusDetectedAgent;
    private int _agentRowsShown;

    private void InitializeAgents()
    {
        _agents.Changed += () => Dispatcher.BeginInvoke(UpdateAgentCard);
        UpdateAgentCard();
    }

    /// <summary>How much taller the Home tab is with the Agents card showing.</summary>
    private double AgentCardExtra => _agentRowsShown == 0 ? 0 : AgentCardChrome + (_agentRowsShown * AgentRowHeight);

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
        AgentsCard.Visibility = rows.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

        if (_agentRowsShown != rows.Length)
        {
            // The Home tab is taller with more rows.
            _agentRowsShown = rows.Length;
            Refresh();
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
        private static readonly Brush Working = Frozen(0x4C, 0xC2, 0xFF);
        private static readonly Brush Attention = Frozen(0xFF, 0xB9, 0x00);
        private static readonly Brush Done = Frozen(0x6C, 0xCB, 0x5F);
        private static readonly Brush Quiet = Frozen(0x80, 0x80, 0x88);

        public AgentEntry Entry { get; } = entry;

        public string Title => string.IsNullOrEmpty(Entry.FolderName) ? Entry.DisplayName : $"{Entry.DisplayName} · {Entry.FolderName}";

        /// <summary>Where it runs: the program a found agent is in, or Notch's terminal.</summary>
        public string Where => Entry.Source == AgentSource.Notch ? "Notch terminal" : Entry.Host ?? "";

        public string StateText => Entry.StateText;

        public string Tip => Entry.Source == AgentSource.Notch ? "Show this session" : "Bring its window to the front";

        public Brush Brush => Entry.State switch
        {
            AgentState.Working => Working,
            AgentState.NeedsInput => Attention,
            AgentState.Done => Done,
            _ => Quiet,
        };

        private static Brush Frozen(byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }
    }
}
