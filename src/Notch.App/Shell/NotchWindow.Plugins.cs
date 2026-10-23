using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Notch.Core.Plugins;

namespace Notch.App.Shell;

// The Plugins tab: the cards plugins publish through IPluginCards.
public partial class NotchWindow
{
    private const int PluginCardColumns = 3;
    private const int MaxVisiblePluginCardRows = 3;

    // A card is 95 high plus a 6 margin on each side.
    private const double PluginCardRowHeight = 107;

    private int _pluginCardCount;

    /// <summary>Tall enough for the rows of cards there are, up to the point where the tab scrolls instead.</summary>
    private double PluginsTabHeight
    {
        get
        {
            int rows = (_pluginCardCount + PluginCardColumns - 1) / PluginCardColumns;
            return 76 + (Math.Clamp(rows, 1, MaxVisiblePluginCardRows) * PluginCardRowHeight);
        }
    }

    private void InitializePlugins()
    {
        _pluginCards.Changed += OnPluginCardsChanged;
        UpdatePluginCards();
    }

    private void OnPluginCardsChanged(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(UpdatePluginCards);

    private void UpdatePluginCards()
    {
        PluginCardRow[] rows = [.. _pluginCards.Snapshot().Select(e => new PluginCardRow(e.Card))];
        PluginCards.ItemsSource = rows;
        _pluginCardCount = rows.Length;
        TabPlugins.Visibility = rows.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (HomeView view in HomeViewsOf<PluginCardView>())
        {
            view.Refresh();
        }

        HomeContentChanged();

        if (_tab == NotchTab.Plugins)
        {
            if (rows.Length == 0)
            {
                // The last card went away (its plugin was switched off) while the tab was open.
                TabHome.IsChecked = true;
            }
            else
            {
                Refresh();
            }
        }
    }

    /// <summary>
    /// Moves on the clocks plugins asked Notch to keep (<c>CountingFrom</c>) on whatever is showing: the pill, the cards
    /// and a page's figures. Runs every second.
    /// </summary>
    private void TickPluginClocks()
    {
        if (!_expanded && _pillActivity is { CountingFrom: { } from, AgentState: null } && !IsMedia(_pillActivity))
        {
            CompactDetail.Text = Notch.Core.Activities.ElapsedClock.Since(from);
            CompactDetail.Visibility = Visibility.Visible;
        }

        if (!_expanded)
        {
            return;
        }

        bool cardsCount = _pluginCards.Snapshot().Any(e => e.Card.CountingFrom is not null);
        if (cardsCount && _tab == NotchTab.Plugins)
        {
            PluginCards.ItemsSource = _pluginCards.Snapshot().Select(e => new PluginCardRow(e.Card)).ToList();
        }

        if (cardsCount && _tab == NotchTab.Home)
        {
            foreach (HomeView view in HomeViewsOf<PluginCardView>())
            {
                view.Refresh();
            }
        }

        if (_tab == NotchTab.Page && CurrentPage?.Page is { View: null } page && page.Stats.Any(s => s.CountingFrom is not null))
        {
            PageStats.ItemsSource = page.Stats.Select(s => new PageStatRow(s)).ToList();
        }
    }

    private void OnPluginCardClicked(object sender, MouseButtonEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is PluginCardRow row)
        {
            row.Card.Clicked?.Invoke();
        }
    }

    private sealed class PluginCardRow(PluginCard card)
    {
        public PluginCard Card { get; } = card;

        public string Label => Card.Label;

        public string? Value => Card.CountingFrom is { } from ? Notch.Core.Activities.ElapsedClock.Since(from) : Card.Value;

        public string? Detail => Card.Detail;

        public double Progress => Math.Clamp(Card.Progress ?? 0, 0, 1);

        // Looked up when the row is made; the rows are rebuilt when the theme changes.
        public Brush ValueBrush { get; } = card.Color is { } color
            ? ThemeManager.Brush(color)
            : (Brush)Application.Current.FindResource("TextBrush");

        public Brush AccentBrush { get; } = card.Color is { } color
            ? ThemeManager.Brush(color)
            : (Brush)Application.Current.FindResource("AccentBrush");

        public Visibility ProgressVisibility => Card.Progress is null ? Visibility.Collapsed : Visibility.Visible;

        public Cursor Cursor => Card.Clicked is null ? Cursors.Arrow : Cursors.Hand;
    }
}
