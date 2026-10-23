using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Notch.Core.Plugins;
using Notch.Platform.Display;

namespace Notch.App.Shell;

// Plugin pages: a tab per IPluginPages page, with figures on top and a console below.
public partial class NotchWindow
{
    private const int MaxHistory = 50;

    private readonly Dictionary<(string PluginId, string PageId), RadioButton> _pageTabs = [];
    private readonly List<string> _inputHistory = [];
    private int _historyIndex;
    private (string PluginId, string PageId)? _pageKey;
    private PageBlocks _pageBlocks = null!;
    private readonly System.Windows.Threading.DispatcherTimer _armTimer = new() { Interval = TimeSpan.FromSeconds(3.5) };

    /// <summary>The label of the action that asked to be clicked again, or null.</summary>
    private string? _armedAction;

    /// <summary>The function that made the element a page of the plugin's own is showing, or null.</summary>
    private Func<object>? _shownPageView;

    /// <summary>A text box in a page the plugin draws itself has the keyboard.</summary>
    private bool _pageViewKeyboard;

    /// <summary>The page that went away while it was open, and when: its plugin may be restarting, and it comes back to it.</summary>
    private ((string PluginId, string PageId) Key, DateTime At)? _pageLost;

    private static readonly TimeSpan PageReturnTime = TimeSpan.FromSeconds(5);

    private const double LargePageWidth = 920;
    private const double LargePageHeight = 540;
    private const double CompactPageWidth = 640;
    private const double SmallestCompactPage = 180;

    /// <summary>How tall a compact page's content made the notch, measured after each update.</summary>
    private double _compactPageHeight = LargePageHeight;

    private bool CompactPage => CurrentPage?.Page.Size == PluginPageSize.Compact;

    private Shape PageShape() => CompactPage
        ? new Shape(CompactPageWidth, _compactPageHeight, 32)
        : new Shape(LargePageWidth, LargePageHeight, 32);

    /// <summary>A compact page is as tall as what it shows: measured once the new content is laid out.</summary>
    private void FitCompactPage()
    {
        if (!CompactPage || _tab != NotchTab.Page)
        {
            return;
        }

        Dispatcher.BeginInvoke(() =>
        {
            if (!CompactPage || _tab != NotchTab.Page || !PagePanel.IsVisible)
            {
                return;
            }

            // The content's width is fixed by the shape; its height is whatever it asks for there.
            double width = CompactPageWidth - 36;
            PagePanel.Measure(new Size(width, double.PositiveInfinity));
            double top = PagePanel.TranslatePoint(new Point(0, 0), ExpandedLayer).Y - PagePanel.Margin.Top;
            double height = Math.Clamp(Math.Ceiling(top + PagePanel.DesiredSize.Height + 32), SmallestCompactPage, LargePageHeight);
            if (Math.Abs(height - _compactPageHeight) >= 1)
            {
                _compactPageHeight = height;
                Refresh();
            }
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>The page the tab is showing, or null when another tab is.</summary>
    private PluginPageEntry? CurrentPage => _pageKey is { } key
        ? _pluginPages.Snapshot().FirstOrDefault(e => e.PluginId == key.PluginId && e.Page.Id == key.PageId)
        : null;

    /// <summary>The page is showing and has an input line, so keystrokes must reach the window.</summary>
    private bool PageTakesInput => _tab == NotchTab.Page && CurrentPage?.Page is { } p
        && (p.Input is not null || p.Accept is not null || (p.View is null ? p.Choices.Count == 0 && p.Blocks.Any(b => b is PluginTextField) : _pageViewKeyboard));

    private void InitializePages()
    {
        _pluginPages.Changed += OnPluginPagesChanged;
        _pageBlocks = new PageBlocks(PageBlockStack);
        PageInput.KeyDown += OnPageInputKeyDown;
        _armTimer.Tick += (_, _) => DisarmAction();
        PageInput.TextChanged += (_, _) =>
        {
            PageInputHint.Visibility = PageInput.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            PageInputError.Visibility = Visibility.Collapsed;
        };
        PageSend.Click += (_, _) => SubmitPageInput();
        PageBack.Click += (_, _) => CurrentPage?.Page.Back?.Invoke();

        // A click in a box of the plugin's own view asks for the keyboard, as one in a widget on Home does.
        PageViewHost.AddHandler(PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler((_, e) =>
        {
            if (e.OriginalSource is DependencyObject source && PluginViews.FindTextBox(source) is { } box)
            {
                SetPageViewKeyboard(true);
                box.Focus();
            }
        }), handledEventsToo: true);
        PageViewHost.AddHandler(LostKeyboardFocusEvent, new KeyboardFocusChangedEventHandler((_, e) =>
        {
            if (e.OriginalSource is TextBox && !(e.NewFocus is DependencyObject next && PageViewHost.IsAncestorOf(next)))
            {
                SetPageViewKeyboard(false);
            }
        }), handledEventsToo: true);
        SyncPageTabs();
    }

    /// <summary>The notch takes the keyboard while a box in a page's own view is being typed into, and gives it back after.</summary>
    private void SetPageViewKeyboard(bool on)
    {
        if (_pageViewKeyboard == on)
        {
            return;
        }

        _pageViewKeyboard = on;
        if (on)
        {
            NoteForegroundWindow();
            UpdateKeyboardInteraction();
            Activate();
        }
        else
        {
            bool handBack = IsActive && _lastOtherWindow != 0 && !KeyboardInUse;
            UpdateKeyboardInteraction();
            if (handBack)
            {
                OverlayWindow.SetForeground(_lastOtherWindow);
            }
        }
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
                if (_tab == NotchTab.Home && _expanded && _pageLost is { } lost && DateTime.UtcNow - lost.At < PageReturnTime
                    && _pageTabs.TryGetValue(lost.Key, out RadioButton? back))
                {
                    // A plugin restarted (a rebuild, or new options) and its page is back: so is the notch.
                    _pageLost = null;
                    back.IsChecked = true;
                }
                else if (_tab == NotchTab.Page)
                {
                    if (CurrentPage is null)
                    {
                        // The page went away (its plugin was switched off, or it closed the page) while it was open.
                        _pageLost = _pageKey is { } gone ? (gone, DateTime.UtcNow) : null;
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

    /// <summary>
    /// Switches to a plugin's page, named "plugin-id/page-id", without opening the notch. False when there is no
    /// such page, for instance because its plugin is not running.
    /// </summary>
    public bool OpenPluginPage(string key)
    {
        if (!PluginManager.TryParsePageKey(key, out string pluginId, out string pageId)
            || !_pageTabs.TryGetValue((pluginId, pageId), out RadioButton? tab))
        {
            return false;
        }

        tab.IsChecked = true;
        return true;
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

        if (page.View is { } view)
        {
            ShowPageView(page, view);
            return;
        }

        _shownPageView = null;
        PageViewHost.Child = null;
        PageViewHost.Visibility = Visibility.Collapsed;
        PageStats.ItemsSource = page.Stats.Select(s => new PageStatRow(s)).ToList();

        bool listed = page.Choices.Count > 0;
        bool blocks = !listed && page.Blocks.Count > 0;
        PageChoices.ItemsSource = page.Choices.Select(c => new PageChoiceRow(c)).ToList();
        PageChoicesScroll.Visibility = listed ? Visibility.Visible : Visibility.Collapsed;
        PageBlocksScroll.Visibility = blocks ? Visibility.Visible : Visibility.Collapsed;
        PageConsoleCard.Visibility = listed || blocks ? Visibility.Collapsed : Visibility.Visible;
        if (blocks)
        {
            _pageBlocks.Render(page.Blocks);
        }
        PageStats.Visibility = page.Stats.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        ShowPageChrome(page);
    }

    /// <summary>What every page has around its body: the back button, the status line, the buttons and the input line.</summary>
    private void ShowPageChrome(PluginPage page)
    {
        FitCompactPage();
        PageBack.Visibility = page.Back is null ? Visibility.Collapsed : Visibility.Visible;
        PageBack.Content = "‹  " + (page.BackLabel ?? "Back");
        PageStatus.Text = page.Status ?? "";
        PageStatus.ToolTip = page.Status;
        PageStatus.Visibility = string.IsNullOrWhiteSpace(page.Status) ? Visibility.Collapsed : Visibility.Visible;
        PageInputRow.Visibility = page.Input is null && page.Accept is null ? Visibility.Collapsed : Visibility.Visible;
        PageInputHint.Text = page.InputHint ?? "";
        ShowActions(page.Actions);
        UpdateKeyboardInteraction();
    }

    /// <summary>A page the plugin draws itself: its element in place of the figures and the body, with the top row and input line around it.</summary>
    private void ShowPageView(PluginPage page, Func<object> view)
    {
        if (!ReferenceEquals(view, _shownPageView))
        {
            _shownPageView = view;
            PageViewHost.Child = PluginViews.Create(view);
        }

        PageViewHost.Visibility = Visibility.Visible;
        PageStats.Visibility = PageChoicesScroll.Visibility = PageBlocksScroll.Visibility = PageConsoleCard.Visibility = Visibility.Collapsed;
        ShowPageChrome(page);
    }

    /// <summary>Rebuilds the page's button row. Called on every update of the page, so the armed state is kept across it.</summary>
    private void ShowActions(IReadOnlyList<PluginAction> actions)
    {
        PageActions.Children.Clear();
        foreach (PluginAction action in actions)
        {
            bool armed = _armedAction == action.Label;
            var button = new Button
            {
                Style = (Style)FindResource("PillButton"),
                Content = armed ? "Click again" : action.Label,
                IsEnabled = action.Enabled,
                Opacity = action.Enabled ? 1 : 0.4,
                ToolTip = action.Hint,
            };

            if (action.Color is { } color)
            {
                button.Foreground = ThemeManager.Brush(color);
            }

            button.Click += (_, _) => OnPageActionClicked(action);
            PageActions.Children.Add(button);
        }
    }

    private void OnPageActionClicked(PluginAction action)
    {
        if (!action.Enabled)
        {
            return;
        }

        if (action.Confirm && _armedAction != action.Label)
        {
            // First click only arms it; the label says so for a few seconds.
            _armedAction = action.Label;
            _armTimer.Stop();
            _armTimer.Start();
            ShowActions(CurrentPage?.Page.Actions ?? []);
            return;
        }

        DisarmAction();
        action.Clicked?.Invoke();
    }

    private void DisarmAction()
    {
        _armTimer.Stop();
        if (_armedAction is not null)
        {
            _armedAction = null;
            ShowActions(CurrentPage?.Page.Actions ?? []);
        }
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

    private async void SubmitPageInput()
    {
        string text = PageInput.Text.Trim();
        if (text.Length == 0 || CurrentPage?.Page is not { } page || (page.Input is null && page.Accept is null))
        {
            return;
        }

        RememberInput(text);
        if (page.Accept is { } accept)
        {
            // The plugin decides whether the line goes; until then it stays where the user can fix it.
            string typed = PageInput.Text;
            string? problem = await Task.Run(() => accept(text));
            if (problem is null)
            {
                if (PageInput.Text == typed)
                {
                    PageInput.Clear();
                }
            }
            else if (PageInput.Text == typed)
            {
                PageInputError.Text = problem;
                PageInputError.Foreground = ThemeManager.Brush(Notch.Core.Activities.GlowColor.Red);
                PageInputError.Visibility = Visibility.Visible;
            }

            return;
        }

        PageInput.Clear();
        page.Input?.Invoke(text);
    }

    private void RememberInput(string text)
    {
        if (_inputHistory.Count == 0 || _inputHistory[^1] != text)
        {
            _inputHistory.Add(text);
            if (_inputHistory.Count > MaxHistory)
            {
                _inputHistory.RemoveAt(0);
            }
        }

        _historyIndex = _inputHistory.Count;
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

    private void OnPageChoiceClick(object sender, RoutedEventArgs e) =>
        ((sender as FrameworkElement)?.Tag as PageChoiceRow)?.Choice.Clicked?.Invoke();

    private sealed class PageChoiceRow(PluginChoice choice)
    {
        public PluginChoice Choice => choice;

        public string Label => choice.Label;

        public string? Value => choice.Value;

        public string? Detail => choice.Detail;

        public Brush ValueBrush { get; } = choice.Color is { } color
            ? ThemeManager.Brush(color)
            : (Brush)Application.Current.FindResource("TextBrush");
    }

    private sealed class PageStatRow(PluginStat stat)
    {
        public string Label => stat.Label;

        public string? Value => stat.CountingFrom is { } from ? Notch.Core.Activities.ElapsedClock.Since(from) : stat.Value;

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
