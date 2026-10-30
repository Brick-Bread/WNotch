using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Notch.App.Agents;
using Notch.Core.Agents;
using Notch.Core.Widgets;

namespace Notch.App.Shell;

// The usage widget: how much of Claude's and Codex's plan limits is used, and what opencode spent today.
public partial class NotchWindow
{
    private UsageService? _usage;

    /// <summary>Shows what <paramref name="usage"/> knows on the usage widget, from now on.</summary>
    internal void AttachUsage(UsageService usage)
    {
        _usage = usage;
        usage.Changed += () => Dispatcher.BeginInvoke(() =>
        {
            foreach (UsageView view in HomeViewsOf<UsageView>())
            {
                view.Refresh();
            }

            HomeContentChanged();
        });
    }

    private sealed class UsageView(NotchWindow owner, HomeWidget widget) : HomeView(owner, widget)
    {
        private readonly StackPanel _rows = new() { Margin = new Thickness(0, 4, 0, 0) };
        private DateTime _shownAt;

        public override bool IsEmpty => Owner._usage is not { } usage
            || !((ShowsClaude && (usage.Claude is not null || usage.ClaudeProblem is not null))
                || (Widget.Flag("codex") && usage.Codex is not null)
                || (Widget.Flag("opencode") && usage.OpenCodeToday is not null));

        private bool ShowsClaude => Widget.Flag("claude") && Owner._settings.ClaudeUsage;

        protected override string EmptyText => "No agent usage yet";

        protected override FrameworkElement CreateContent()
        {
            var stack = new StackPanel();
            stack.Children.Add(Label("Usage"));
            stack.Children.Add(_rows);
            Border card = Card();
            card.Child = stack;
            return card;
        }

        public override void OptionsChanged()
        {
            Refresh();
            Owner.HomeContentChanged();
        }

        public override void Tick(DateTime now)
        {
            _ = Owner._usage?.RefreshAsync();

            // "In 12 min" moves on even when the figures do not.
            if (now - _shownAt > TimeSpan.FromSeconds(30))
            {
                Refresh();
            }
        }

        public override void Refresh()
        {
            _shownAt = DateTime.Now;
            _rows.Children.Clear();
            foreach (FrameworkElement section in Sections())
            {
                section.Margin = new Thickness(0, _rows.Children.Count == 0 ? 2 : 10, 0, 0);
                _rows.Children.Add(section);
            }
        }

        private List<FrameworkElement> Sections()
        {
            var sections = new List<FrameworkElement>();
            if (Owner._usage is not { } usage)
            {
                return sections;
            }

            if (ShowsClaude && (usage.Claude is not null || usage.ClaudeProblem is not null))
            {
                sections.Add(Section("Claude", usage.Claude, usage.ClaudeProblem));
            }

            if (Widget.Flag("codex") && usage.Codex is { } codex)
            {
                sections.Add(Section("Codex", codex, codex.At < DateTime.UtcNow.AddHours(-1) ? "As of " + codex.At.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) : null));
            }

            if (Widget.Flag("opencode") && usage.OpenCodeToday is { } today)
            {
                var line = new DockPanel();
                var name = new TextBlock { Text = "opencode today", FontSize = 12, FontWeight = FontWeights.SemiBold, Width = 110 };
                line.Children.Add(name);
                string cost = today.Cost > 0 ? today.Cost.ToString("$0.00", CultureInfo.InvariantCulture) + " · " : "";
                line.Children.Add(Secondary($"{cost}{UsageMeters.TokenText(today.Tokens)} tokens · {today.Steps} steps", 12));
                sections.Add(line);
            }

            return sections;
        }

        /// <summary>One agent: its name, and a bar for each of its limits.</summary>
        private static StackPanel Section(string agent, UsageReading? reading, string? note)
        {
            var section = new StackPanel();
            var head = new DockPanel();
            head.Children.Add(new TextBlock { Text = agent, FontSize = 12, FontWeight = FontWeights.SemiBold });
            if (note is not null)
            {
                TextBlock problem = Secondary(note, 10.5);
                problem.Margin = new Thickness(8, 1, 0, 0);
                problem.VerticalAlignment = VerticalAlignment.Center;
                head.Children.Add(problem);
            }

            section.Children.Add(head);
            foreach (UsageWindow window in reading?.Windows ?? [])
            {
                section.Children.Add(Bar(window));
            }

            return section;
        }

        private static Grid Bar(UsageWindow window)
        {
            var row = new Grid { Margin = new Thickness(0, 4, 0, 0) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });

            TextBlock label = Secondary(window.Label, 11);
            label.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(label);

            var bar = new ProgressBar { Height = 5, Maximum = 100, Value = window.UsedPercent, VerticalAlignment = VerticalAlignment.Center };
            bar.SetResourceReference(Control.ForegroundProperty, window.UsedPercent >= 90 ? "DangerBrush" : window.UsedPercent >= 70 ? "AttentionBrush" : "AccentBrush");
            bar.SetResourceReference(Control.BackgroundProperty, "TrackBrush");
            Grid.SetColumn(bar, 1);
            row.Children.Add(bar);

            var percent = new TextBlock
            {
                Text = $"{Math.Round(window.UsedPercent):0}%",
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                TextAlignment = TextAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Typography = { NumeralAlignment = FontNumeralAlignment.Tabular },
            };
            Grid.SetColumn(percent, 2);
            row.Children.Add(percent);

            TextBlock resets = Secondary(window.ResetsAt is { } at ? "resets " + UsageMeters.ResetText(at.ToLocalTime(), DateTime.Now) : "", 10.5);
            resets.Margin = new Thickness(10, 0, 0, 0);
            resets.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(resets, 3);
            row.Children.Add(resets);
            return row;
        }
    }
}
