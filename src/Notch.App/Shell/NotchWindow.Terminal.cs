using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using Notch.App.Terminal;
using Notch.Core.Terminal;
using Notch.Platform.Display;

namespace Notch.App.Shell;

// Tab switching and the Terminal tab.
public partial class NotchWindow
{
    private NotchTab _tab = NotchTab.Home;
    private string _terminalFolder = "";

    private enum NotchTab
    {
        Home,
        Terminal,
        Stats,
    }

    /// <summary>The terminal takes keyboard input, so the notch must not close under the user while it has focus.</summary>
    private bool TerminalHasFocus => _expanded && _tab == NotchTab.Terminal && IsActive;

    /// <summary>Selects a tab by name ("home", "terminal", "stats"); unknown names are ignored.</summary>
    public void ShowTab(string name)
    {
        if (Enum.TryParse(name, ignoreCase: true, out NotchTab tab))
        {
            (tab switch
            {
                NotchTab.Terminal => TabTerminal,
                NotchTab.Stats => TabStats,
                _ => TabHome,
            }).IsChecked = true;
        }
    }

    /// <summary>Switches to the Terminal tab and starts a session there.</summary>
    public void OpenTerminal(TerminalProfile profile)
    {
        TabTerminal.IsChecked = true;
        OpenSession(profile);
    }

    private void InitializeTerminal()
    {
        TerminalHost.Child = _terminal.Bridge.Control;
        SessionStrip.ItemsSource = _terminal.Sessions;
        _terminal.Sessions.CollectionChanged += (_, _) => UpdateTerminalEmpty();

        TabHome.Checked += (_, _) => SelectTab(NotchTab.Home);
        TabTerminal.Checked += (_, _) => SelectTab(NotchTab.Terminal);
        TabStats.Checked += (_, _) => SelectTab(NotchTab.Stats);

        NewClaude.Click += (_, _) => OpenSession(TerminalProfile.Claude);
        NewCodex.Click += (_, _) => OpenSession(TerminalProfile.Codex);
        NewShell.Click += (_, _) => OpenSession(TerminalProfile.Shell);
        TerminalFolder.Click += (_, _) => ShowFolderMenu();

        _terminalFolder = _settings.RecentFolders.FirstOrDefault(Directory.Exists)
            ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        UpdateFolderButton();

        // Clicking another window is how the user leaves a focused terminal.
        Deactivated += (_, _) =>
        {
            if (!Island.IsMouseOver)
            {
                SetExpanded(false);
            }
        };
    }

    private void SelectTab(NotchTab tab)
    {
        _tab = tab;
        HomePanel.Visibility = tab == NotchTab.Home ? Visibility.Visible : Visibility.Collapsed;
        TerminalPanel.Visibility = tab == NotchTab.Terminal ? Visibility.Visible : Visibility.Collapsed;
        StatsPanel.Visibility = tab == NotchTab.Stats ? Visibility.Visible : Visibility.Collapsed;

        Refresh();
        UpdateTerminalInteraction();
        UpdateStatsTimer();
        if (tab == NotchTab.Terminal)
        {
            FocusTerminal();
        }
    }

    /// <summary>
    /// The notch normally refuses activation so it never steals focus. While the terminal is
    /// showing it has to accept it, or it could not receive keystrokes.
    /// </summary>
    private void UpdateTerminalInteraction()
    {
        bool interactive = _expanded && _tab == NotchTab.Terminal;
        if (_hwnd != 0)
        {
            OverlayWindow.SetNoActivate(_hwnd, !interactive);
        }

        _terminal.IsViewing = interactive;
    }

    private void FocusTerminal()
    {
        if (_terminal.Sessions.Count == 0 || !_expanded)
        {
            return;
        }

        Activate();
        _terminal.Bridge.Focus();
    }

    private async void OpenSession(TerminalProfile profile)
    {
        _settings.RememberFolder(_terminalFolder);
        _settingsStore.Save(_settings);

        try
        {
            await _terminal.OpenAsync(profile, _terminalFolder);
            FocusTerminal();
        }
        catch (Exception e)
        {
            // Most likely the WebView2 runtime is missing or broken.
            TerminalEmpty.Text = "The terminal could not start: " + e.Message;
            TerminalEmpty.Visibility = Visibility.Visible;
        }
    }

    private void UpdateTerminalEmpty() =>
        TerminalEmpty.Visibility = _terminal.Sessions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void OnSessionTabClicked(object sender, MouseButtonEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is TerminalSession session)
        {
            _terminal.Activate(session);
            FocusTerminal();
        }
    }

    private void OnSessionCloseClicked(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is TerminalSession session)
        {
            _terminal.Close(session);
            FocusTerminal();
        }
    }

    private void ShowFolderMenu()
    {
        var menu = new ContextMenu { PlacementTarget = TerminalFolder };
        foreach (string folder in _settings.RecentFolders.Where(Directory.Exists))
        {
            var item = new MenuItem { Header = folder.Replace("_", "__"), IsChecked = folder == _terminalFolder };
            item.Click += (_, _) => SetTerminalFolder(folder);
            menu.Items.Add(item);
        }

        if (menu.Items.Count > 0)
        {
            menu.Items.Add(new Separator());
        }

        var browse = new MenuItem { Header = "Browse…" };
        browse.Click += (_, _) => BrowseForFolder();
        menu.Items.Add(browse);
        menu.IsOpen = true;
    }

    private void BrowseForFolder()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Folder for new terminal sessions",
            InitialDirectory = _terminalFolder,
        };

        if (dialog.ShowDialog(this) == true)
        {
            SetTerminalFolder(dialog.FolderName);
        }
    }

    private void SetTerminalFolder(string folder)
    {
        _terminalFolder = folder;
        _settings.RememberFolder(folder);
        _settingsStore.Save(_settings);
        UpdateFolderButton();
    }

    private void UpdateFolderButton()
    {
        string name = Path.GetFileName(_terminalFolder.TrimEnd('\\', '/'));
        TerminalFolder.Content = "\U0001F4C1 " + (name.Length > 0 ? name : _terminalFolder);
        TerminalFolder.ToolTip = $"New sessions start in {_terminalFolder}";
    }
}
