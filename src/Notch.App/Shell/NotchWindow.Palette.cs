using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Notch.Core.Shell;
using Notch.Core.Widgets;
using Notch.Platform.Display;
using PlatformKeyboard = Notch.Platform.Input.Keyboard;
using Notch.Platform.Input;

namespace Notch.App.Shell;

/// <summary>One thing the command palette can do.</summary>
/// <param name="Subtitle">A word or two on the right: where it goes, or what kind of thing it is.</param>
/// <param name="Keywords">Extra words that find it, such as "preferences" for the settings.</param>
public sealed record PaletteEntry(string Title, string? Subtitle, string? Keywords, Action Run);

// The command palette: a search box over everything the notch can do, opened by its own hotkey.
public partial class NotchWindow
{
    private const int PaletteHotkeyId = 2;
    private const double PaletteTabHeight = 392;
    private const int PaletteRows = 7;

    private bool _paletteHotkeyRegistered;
    private NotchTab _tabBeforePalette = NotchTab.Home;
    private RadioButton? _radioBeforePalette;
    private bool _paletteOpenedClosed;
    private PaletteEntry[] _paletteShown = [];
    private int _paletteIndex;

    /// <summary>What the palette lists beyond the notch's own entries (plugins, say). Asked each time it opens.</summary>
    public Func<IEnumerable<PaletteEntry>>? PaletteExtras { get; set; }

    /// <summary>What is wrong with the palette hotkey in the settings; null when it works or none is wanted.</summary>
    public string? PaletteHotkeyProblem { get; private set; }

    private bool PaletteShowing => _tab == NotchTab.Palette;

    private void InitializePalette()
    {
        PaletteInput.TextChanged += (_, _) =>
        {
            PaletteHint.Visibility = PaletteInput.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            _paletteIndex = 0;
            ShowPaletteRows();
        };
        PaletteInput.PreviewKeyDown += OnPaletteKeyDown;
    }

    private void RegisterPaletteHotkey()
    {
        if (_hwnd == 0)
        {
            return;
        }

        if (_paletteHotkeyRegistered)
        {
            PlatformKeyboard.UnregisterHotkey(_hwnd, PaletteHotkeyId);
            _paletteHotkeyRegistered = false;
        }

        PaletteHotkeyProblem = null;
        string text = _settings.PaletteHotkey.Trim();
        if (text.Length == 0)
        {
            return;
        }

        if (!Hotkey.TryParse(text, out Hotkey hotkey))
        {
            PaletteHotkeyProblem = $"\"{text}\" is not a hotkey Notch can use for the palette.";
            return;
        }

        _paletteHotkeyRegistered = PlatformKeyboard.RegisterHotkey(_hwnd, PaletteHotkeyId, hotkey);
        if (!_paletteHotkeyRegistered)
        {
            PaletteHotkeyProblem = $"{hotkey} is already used by Windows or another app (palette hotkey).";
        }
    }

    /// <summary>Opens the palette, or closes it when it is already open.</summary>
    public void TogglePalette()
    {
        if (_hiddenForFullscreen)
        {
            return;
        }

        if (PaletteShowing)
        {
            ClosePalette(handBack: true);
            return;
        }

        // The window that has the keyboard now is where it goes back to afterwards.
        nint current = OverlayWindow.GetForeground();
        if (current != 0 && current != _hwnd)
        {
            _lastOtherWindow = current;
        }

        _paletteOpenedClosed = !_expanded;
        _tabBeforePalette = _tab == NotchTab.Palette ? NotchTab.Home : _tab;

        // No tab button is lit while the palette is up; the one that was is lit again when it closes.
        _radioBeforePalette = TabStrip.Children.OfType<RadioButton>().FirstOrDefault(r => r.IsChecked == true);
        if (_radioBeforePalette is not null)
        {
            _radioBeforePalette.IsChecked = false;
        }

        _hoverTimer.Stop();
        _hotkeyHold = true;
        SetExpanded(true);
        SelectTab(NotchTab.Palette);
        PaletteInput.Clear();
        _paletteIndex = 0;
        ShowPaletteRows();

        // The window accepts the keyboard only after it has been told to; focus follows.
        Dispatcher.BeginInvoke(() =>
        {
            Activate();
            PaletteInput.Focus();
            System.Windows.Input.Keyboard.Focus(PaletteInput);
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    private void ClosePalette(bool handBack)
    {
        if (!PaletteShowing)
        {
            return;
        }

        if (_radioBeforePalette is { } previous)
        {
            // Checking it again selects its tab.
            previous.IsChecked = true;
        }
        else
        {
            SelectTab(_tabBeforePalette);
        }

        _radioBeforePalette = null;
        bool give = handBack && IsActive && _lastOtherWindow != 0;
        if (_paletteOpenedClosed)
        {
            SetExpanded(false);
        }

        if (give)
        {
            OverlayWindow.SetForeground(_lastOtherWindow);
        }
    }

    private void OnPaletteKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                e.Handled = true;
                ClosePalette(handBack: true);
                break;
            case Key.Down:
                e.Handled = true;
                MovePalette(1);
                break;
            case Key.Up:
                e.Handled = true;
                MovePalette(-1);
                break;
            case Key.Enter:
                e.Handled = true;
                RunPaletteEntry(_paletteIndex);
                break;
        }
    }

    private void MovePalette(int step)
    {
        if (_paletteShown.Length == 0)
        {
            return;
        }

        _paletteIndex = (_paletteIndex + step + _paletteShown.Length) % _paletteShown.Length;
        ShowPaletteRows();
    }

    private void ShowPaletteRows()
    {
        string query = PaletteInput.Text;
        IReadOnlyList<PaletteEntry> all = BuildPaletteEntries(query);
        _paletteShown = [.. PaletteSearch.Rank(all, query, entry => entry.Title, entry => entry.Keywords, PaletteRows)];
        _paletteIndex = Math.Clamp(_paletteIndex, 0, Math.Max(0, _paletteShown.Length - 1));

        PaletteList.ItemsSource = _paletteShown
            .Select((entry, index) => new PaletteRow(entry, index == _paletteIndex))
            .ToList();
    }

    private void OnPaletteRowClicked(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { Tag: PaletteRow row })
        {
            e.Handled = true;
            RunPaletteEntry(Array.IndexOf(_paletteShown, row.Entry));
        }
    }

    private void RunPaletteEntry(int index)
    {
        if (index < 0 || index >= _paletteShown.Length)
        {
            return;
        }

        PaletteEntry entry = _paletteShown[index];

        // Closed first, so an entry that opens a tab or the settings has the notch to itself.
        bool opensSomething = entry.Subtitle is "Tab" or "Settings";
        ClosePalette(handBack: !opensSomething);
        entry.Run();
    }

    /// <summary>Everything the palette can do right now, in the order shown when nothing is typed.</summary>
    private IReadOnlyList<PaletteEntry> BuildPaletteEntries(string query)
    {
        var entries = new List<PaletteEntry>();

        // A length typed on its own is a timer.
        if (DurationParser.TryParse(query.Trim(), out TimeSpan typed) && typed > TimeSpan.Zero && typed <= TimeSpan.FromHours(24))
        {
            entries.Add(new PaletteEntry($"Start a {DurationParser.Describe(typed)} timer", "Timer", query, () => StartTimer(typed)));
        }

        foreach ((string title, string tab) in new[] { ("Home", "home"), ("Terminal", "terminal"), ("Stats", "stats"), ("Shelf", "shelf") })
        {
            entries.Add(new PaletteEntry($"Open {title}", "Tab", null, () => OpenOnTab(tab)));
        }

        if (_settings.ClipboardHistory)
        {
            entries.Add(new PaletteEntry("Open Clipboard", "Tab", "copied history paste", () => OpenOnTab("clipboard")));
        }

        if (TabPlugins.Visibility == Visibility.Visible)
        {
            entries.Add(new PaletteEntry("Open Plugins", "Tab", null, () => OpenOnTab("plugins")));
        }

        foreach ((string key, RadioButton tab) in _pageTabs.Select(p => (p.Key.PageId, p.Value)))
        {
            RadioButton page = tab;
            entries.Add(new PaletteEntry($"Open {page.Content}", "Plugin page", key, () =>
            {
                page.IsChecked = true;
                RevealNotch();
            }));
        }

        AddTimerEntries(entries);

        foreach (Notch.Core.Terminal.TerminalProfile profile in _terminalProfiles)
        {
            Notch.Core.Terminal.TerminalProfile chosen = profile;
            entries.Add(new PaletteEntry($"New {profile.DisplayName} session", "Terminal", "start launch", () =>
            {
                TabTerminal.IsChecked = true;
                RevealNotch();
                OpenSession(chosen);
            }));
        }

        foreach (Notch.Core.Agents.AgentEntry agent in _agents.Snapshot())
        {
            Notch.Core.Agents.AgentEntry chosen = agent;
            string where = agent.Source == Notch.Core.Agents.AgentSource.Notch ? "Notch terminal" : agent.Host ?? "";
            string name = string.IsNullOrEmpty(agent.FolderName) ? agent.DisplayName : $"{agent.DisplayName} · {agent.FolderName}";
            entries.Add(new PaletteEntry($"Go to {name}", $"{agent.StateText} · {where}".TrimEnd(' ', '·'), "agent session", () => JumpToAgent(chosen)));
        }

        entries.Add(new PaletteEntry("Open settings", "Settings", "preferences options configure", () => SettingsRequested?.Invoke()));

        if (PaletteExtras is { } extras)
        {
            entries.AddRange(extras());
        }

        return entries;
    }

    private void AddTimerEntries(List<PaletteEntry> entries)
    {
        CountdownState state = _countdown.State;
        if (state == CountdownState.Running)
        {
            entries.Add(new PaletteEntry("Pause the timer", "Timer", null, () => ControlTimer(Notch.Core.Automation.TimerAction.Pause)));
        }

        if (state == CountdownState.Paused)
        {
            entries.Add(new PaletteEntry("Resume the timer", "Timer", null, () => ControlTimer(Notch.Core.Automation.TimerAction.Resume)));
        }

        if (state != CountdownState.Idle)
        {
            entries.Add(new PaletteEntry("Cancel the timer", "Timer", "stop reset", () => ControlTimer(Notch.Core.Automation.TimerAction.Stop)));
        }

        entries.Add(new PaletteEntry("Start a Pomodoro", "Timer", "focus break", StartPomodoro));
        foreach (TimerPreset preset in _settings.Timers())
        {
            TimerPreset chosen = preset;
            string name = string.IsNullOrWhiteSpace(preset.Name) ? DurationParser.Describe(preset.Duration) : $"{preset.Name} ({DurationParser.Describe(preset.Duration)})";
            entries.Add(new PaletteEntry($"Start a {name} timer", "Timer", null, () =>
            {
                StartTimer(chosen.Duration);
                if (!string.IsNullOrWhiteSpace(chosen.Name))
                {
                    _timerName = chosen.Name;
                    UpdateTimer();
                }
            }));
        }
    }

    /// <summary>Starts focus and break sessions back to back, as the Pomodoro button on Home does.</summary>
    public void StartPomodoro()
    {
        _pomodoro = new PomodoroCycle(_settings.PomodoroDurations());
        _timerName = null;
        StartCountdown(_pomodoro.CurrentDuration);
    }

    /// <summary>One row of the palette.</summary>
    private sealed class PaletteRow(PaletteEntry entry, bool selected)
    {
        public PaletteEntry Entry { get; } = entry;

        public string Title => Entry.Title;

        public string? Subtitle => Entry.Subtitle;

        /// <summary>The chosen row is lit; the others are clear.</summary>
        public Brush Background => selected
            ? (Brush)Application.Current.FindResource("AccentHoverBrush")
            : Brushes.Transparent;
    }
}
