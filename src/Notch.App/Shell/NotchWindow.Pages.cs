using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Notch.Core.Plugins;

namespace Notch.App.Shell;

// Plugin pages: a tab per IPluginPages page, with figures on top and a console below.
public partial class NotchWindow
{
    private const int MaxHistory = 50;

    private readonly Dictionary<(string PluginId, string PageId), RadioButton> _pageTabs = [];
    private readonly List<string> _inputHistory = [];
    private int _historyIndex;
    private (string PluginId, string PageId)? _pageKey;

    /// <summary>The page the tab is showing, or null when another tab is.</summary>
    private PluginPageEntry? CurrentPage => _pageKey is { } key
        ? _pluginPages.Snapshot().FirstOrDefault(e => e.PluginId == key.PluginId && e.Page.Id == key.PageId)
        : null;

    /// <summary>The page is showing and has an input line, so keystrokes must reach the window.</summary>
    private bool PageTakesInput => _tab == NotchTab.Page && CurrentPage?.Page.Input is not null;

    private void InitializePages()
    {
        _pluginPages.Changed += OnPluginPagesChanged;
        PageInput.KeyDown += OnPageInputKeyDown;
        PageInput.TextChanged += (_, _) =>
            PageInputHint.Visibility = PageInput.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        PageSend.Click += (_, _) => SubmitPageInput();
        SyncPageTabs();
    }

    private void OnPluginPagesChanged(object? sender, PluginPageChangedEventArgs e) =>
        Dispatcher.BeginInvoke(() => HandlePageChange(e));

    private void HandlePageChange(PluginPageChangedEventArgs e)
    {
        var key = (e.PluginId, e.PageId);
        switch (e.Change)
        {
            case PluginPageChange.Pages:
                SyncPageTabs();
                if (_tab == NotchTab.Page)
                {
                    if (CurrentPage is null)
                    {
                        // The page went away (its plugin was switched off, or it closed the page) while it was open.
                        TabHome.IsChecked = true;
                    }
                    else
                    {
                        ShowPageFigures();
                    }
                }

                break;

            case PluginPageChange.Console when _pageKey == key && e.Line is { } line:
                AppendConsole(line);
                break;

            case PluginPageChange.ConsoleCleared when _pageKey == key:
                PageConsole.Clear();
                break;

            case PluginPageChange.OpenRequested when _pageTabs.TryGetValue(key, out RadioButton? tab):
                tab.IsChecked = true;
                SetExpanded(true);
                break;
        }
    }

    /// <summary>Adds, renames and removes the tab buttons so they match the pages that exist.</summary>
    private void SyncPageTabs()
    {
        IReadOnlyList<PluginPageEntry> pages = _pluginPages.Snapshot();
        var wanted = pages.Select(p => (p.PluginId, p.Page.Id)).ToHashSet();

        foreach (var key in _pageTabs.Keys.Where(k => !wanted.Contains(k)).ToList())
        {
            TabStrip.Children.Remove(_pageTabs[key]);
            _pageTabs.Remove(key);
        }

        foreach (PluginPageEntry entry in pages)
        {
            var key = (entry.PluginId, entry.Page.Id);
            if (_pageTabs.TryGetValue(key, out RadioButton? existing))
            {
                existing.Content = entry.Page.Title;
                continue;
            }

            var tab = new RadioButton
            {
                Style = (Style)FindResource("TabButton"),
                Content = entry.Page.Title,
                Tag = key,
            };
            tab.Checked += (_, _) =>
            {
                _pageKey = key;
                SelectTab(NotchTab.Page);
            };
            _pageTabs[key] = tab;
            TabStrip.Children.Add(tab);
        }
    }

    /// <summary>Fills the page from its current state: stats, input line and the whole console.</summary>
    private void ShowPage()
    {
        if (_pageKey is not { } key || CurrentPage is not { } entry)
        {
            return;
        }

        ShowPageFigures();
        PageConsole.Text = string.Join("\n", _pluginPages.ConsoleOf(key.PluginId, key.PageId));
        // Layout has not run for the new text yet, so scrolling now would go nowhere.
        Dispatcher.BeginInvoke(PageConsole.ScrollToEnd, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void ShowPageFigures()
    {
        if (CurrentPage?.Page is not { } page)
        {
            return;
        }

        PageStats.ItemsSource = page.Stats.Select(s => new PageStatRow(s)).ToList();
        PageInputRow.Visibility = page.Input is null ? Visibility.Collapsed : Visibility.Visible;
        PageInputHint.Text = page.InputHint ?? "";
        UpdateKeyboardInteraction();
    }

    private void AppendConsole(string line)
    {
        bool atEnd = PageConsole.VerticalOffset + PageConsole.ViewportHeight >= PageConsole.ExtentHeight - 4;

        PageConsole.AppendText(PageConsole.Text.Length == 0 ? line : "\n" + line);
        if (PageConsole.LineCount > PluginPageBoard.MaxConsoleLines * 2)
        {
            // Wrapped lines count too, so this is only a rough bound; the board holds the real history.
            ShowPage();
        }
        else if (atEnd)
        {
            // The new text is not laid out yet, so scrolling now would stop short.
            Dispatcher.BeginInvoke(PageConsole.ScrollToEnd, System.Windows.Threading.DispatcherPriority.Loaded);
        }
    }

    private void OnPageInputKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                SubmitPageInput();
                e.Handled = true;
                break;

            case Key.Up when _inputHistory.Count > 0:
                _historyIndex = Math.Max(0, _historyIndex - 1);
                SetInput(_inputHistory[_historyIndex]);
                e.Handled = true;
                break;

            case Key.Down when _inputHistory.Count > 0:
                _historyIndex = Math.Min(_inputHistory.Count, _historyIndex + 1);
                SetInput(_historyIndex < _inputHistory.Count ? _inputHistory[_historyIndex] : "");
                e.Handled = true;
                break;
        }
    }

    private void SetInput(string text)
    {
        PageInput.Text = text;
        PageInput.CaretIndex = text.Length;
    }

    private void SubmitPageInput()
    {
        string text = PageInput.Text.Trim();
        if (text.Length == 0 || CurrentPage?.Page.Input is not { } input)
        {
            return;
        }

        if (_inputHistory.Count == 0 || _inputHistory[^1] != text)
        {
            _inputHistory.Add(text);
            if (_inputHistory.Count > MaxHistory)
            {
                _inputHistory.RemoveAt(0);
            }
        }

        _historyIndex = _inputHistory.Count;
        PageInput.Clear();
        input(text);
    }

    private void FocusPageInput()
    {
        if (!_expanded || !PageTakesInput)
        {
            return;
        }

        Activate();
        PageInput.Focus();
    }

    private sealed class PageStatRow(PluginStat stat)
    {
        public string Label => stat.Label;

        public string? Value => stat.Value;

        public string? Detail => stat.Detail;

        public double Progress => Math.Clamp(stat.Progress ?? 0, 0, 1);

        public Visibility ProgressVisibility => stat.Progress is null ? Visibility.Collapsed : Visibility.Visible;

        public Brush ValueBrush { get; } = stat.Color is { } color
            ? ThemeManager.Brush(color)
            : (Brush)Application.Current.FindResource("TextBrush");

        public Brush AccentBrush { get; } = stat.Color is { } color
            ? ThemeManager.Brush(color)
            : (Brush)Application.Current.FindResource("AccentBrush");
    }
}
