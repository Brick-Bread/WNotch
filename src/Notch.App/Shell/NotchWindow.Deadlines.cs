using System.Media;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Notch.Core.Activities;
using Notch.Core.Automation;
using Notch.Core.Deadlines;
using Notch.Core.Widgets;
using Activity = Notch.Core.Activities.Activity;

namespace Notch.App.Shell;

// Deadlines: the next one at the top of the pill, alerts as they come close, and the Deadlines widget on Home.
public partial class NotchWindow
{
    /// <summary>Room the chip takes in the pill, added to the pill's width while it shows.</summary>
    private const double DeadlineChipRoom = 54;

    private readonly DeadlineAlerts _deadlineAlerts = new();

    // Deadlines are told in minutes; a look every few seconds keeps the pill and the alerts on time.
    private readonly DispatcherTimer _deadlineTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private DeadlineBoard _deadlines = new();
    private Activity? _deadlineShown;
    private bool _deadlineChipShown;

    /// <summary>Adds or moves a deadline for <c>notchctl</c>, a link or the webhook.</summary>
    internal Deadline? SetLinkedDeadline(SetDeadlineCommand command) => _deadlines.SetLinked(command.Title, command.Due, command.Detail);

    internal bool RemoveLinkedDeadline(string title) => _deadlines.RemoveLinked(title);

    private TimeSpan DeadlineNear => DeadlineTiming.NearFromHours(_settings.DeadlineNearHours);

    private void InitializeDeadlines(DeadlineBoard board)
    {
        _deadlines = board;
        _deadlines.Changed += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            UpdateDeadlines();
            foreach (DeadlinesView view in HomeViewsOf<DeadlinesView>())
            {
                view.Refresh();
            }

            HomeContentChanged();
        });
        _calendar.Changed += (_, _) => _deadlines.SetCalendar(_calendar.Deadlines);
        _deadlineTimer.Tick += (_, _) => UpdateDeadlines();
        _deadlineTimer.Start();
        OfferDeadlinesWidget();
        UpdateDeadlines();
    }

    /// <summary>Someone whose Home was arranged before deadlines existed gets the widget once, under the agents.</summary>
    private void OfferDeadlinesWidget()
    {
        if (_settings.DeadlinesWidgetOffered)
        {
            return;
        }

        _settings.DeadlinesWidgetOffered = true;
        if (_settings.HomeWidgets.All(w => w.Kind != WidgetKinds.Deadlines) && _settings.HomeWidgets.Count < HomeLayout.MaxWidgets)
        {
            int after = _settings.HomeWidgets.FindLastIndex(w => w.Kind is WidgetKinds.Media or WidgetKinds.Agents);
            _settings.HomeWidgets.Insert(after + 1, new HomeWidget { Kind = WidgetKinds.Deadlines, Width = WidgetWidth.Full });
        }

        _settingsStore.Save(_settings);
    }

    /// <summary>Puts the next deadline in the pill and announces milestones. Cheap: only touches the pill when what it says changed.</summary>
    private void UpdateDeadlines()
    {
        DateTimeOffset now = DateTimeOffset.Now;
        TimeSpan near = DeadlineNear;

        // Checked even with alerts off, so switching them on does not announce what went by meanwhile.
        IReadOnlyList<DeadlineAlert> alerts = _deadlineAlerts.Check(_deadlines.All(), now, d => _deadlines.SnoozedUntil(d) is not null);
        if (alerts.Count > 0 && _settings.DeadlineAlerts)
        {
            DeadlineAlert alert = alerts.MaxBy(a => a.Milestone)!;
            _activities.Publish(DeadlineActivities.ForAlert(alert, near));
            if (_settings.DeadlineSounds && !_hiddenForFullscreen)
            {
                SystemSounds.Exclamation.Play();
            }
        }

        Activity? pill = _settings.DeadlinesInPill ? DeadlineActivities.ForPill(_deadlines.Next(), now, near) : null;
        if (pill == _deadlineShown)
        {
            return;
        }

        _deadlineShown = pill;
        if (pill is null)
        {
            _activities.Remove(DeadlineActivities.PillId);
        }
        else
        {
            _activities.Publish(pill);
        }
    }

    /// <summary>
    /// The chip with the next deadline, beside whatever else the pill shows: how long is left, in the deadline's colour.
    /// Not beside the deadline itself, the glance row (which holds it already) or a level HUD.
    /// </summary>
    private void FillDeadlineChip(Activity top)
    {
        Deadline? next = _settings.DeadlinesInPill ? _deadlines.Next() : null;
        bool show = next is not null
            && top.Id != DeadlineActivities.PillId
            && top.Tier is ActivityTier.Ongoing or ActivityTier.Attention;
        _deadlineChipShown = show;
        DeadlineChip.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show)
        {
            return;
        }

        TimeSpan left = next!.Due - DateTimeOffset.Now;
        GlowColor color = DeadlineTiming.Color(DeadlineTiming.Urgency(left, DeadlineNear));
        DeadlineChipText.Text = DeadlineTiming.Chip(left);
        DeadlineChipText.Foreground = DeadlineChipGlyph.Foreground = ThemeManager.Brush(color);
        DeadlineChip.Background = ThemeManager.Brush(color, 0x2E);
        DeadlineChip.ToolTip = $"{next.Title}, {DeadlineTiming.Describe(left)}";
    }

    /// <summary>Colours the pill's line about a near deadline as its glow is coloured.</summary>
    private void ColorDeadlineDetail(Activity top)
    {
        if (top is { Id: DeadlineActivities.PillId, Glow: { } glow })
        {
            CompactDetail.Foreground = ThemeManager.Brush(glow.Color);
        }
    }

    /// <summary>The deadlines, to add, complete, snooze and edit; the palette's "Report by fri 17:00" lands here too.</summary>
    private sealed class DeadlinesView(NotchWindow owner, HomeWidget widget) : HomeView(owner, widget)
    {
        private const double RowHeight = 34;
        private const int MaxVisibleRows = 6;

        private readonly TextBlock _label = Label("Deadlines");
        private readonly TextBlock _count = Secondary(size: 11);
        private readonly StackPanel _rows = new() { Background = Brushes.Transparent };
        private readonly TextBlock _empty = Secondary("Nothing due. Add one below, start a calendar event's title with \"Due:\", or run notchctl deadline.", 12);
        private readonly TextBox _title = new() { MaxLength = Deadline.MaxTitleLength };
        private readonly TextBox _due = new() { MaxLength = 48, Width = 150 };
        private readonly TextBlock _titleHint = Secondary("Add a deadline", 12);
        private readonly TextBlock _dueHint = Secondary("When? fri 17:00", 12);
        private readonly TextBlock _preview = Secondary(size: 11);
        private string? _editing;
        private string _signature = "";

        protected override double MinHeight => 96;

        protected override FrameworkElement CreateContent()
        {
            _count.Margin = new Thickness(6, 0, 0, 0);
            _count.VerticalAlignment = VerticalAlignment.Bottom;
            var header = new StackPanel { Orientation = Orientation.Horizontal, Children = { _label, _count } };

            var scroll = new ScrollViewer
            {
                Content = _rows,
                MaxHeight = RowHeight * MaxVisibleRows,
                Margin = new Thickness(0, 6, 0, 0),
                VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
                Focusable = false,
            };
            _empty.TextWrapping = TextWrapping.Wrap;
            _empty.Margin = new Thickness(0, 8, 0, 4);

            foreach ((TextBox box, TextBlock hint) in new[] { (_title, _titleHint), (_due, _dueHint) })
            {
                box.SetResourceReference(StyleProperty, "PillTextBox");
                box.Padding = new Thickness(10, 4, 10, 4);
                hint.IsHitTestVisible = false;
                hint.Margin = new Thickness(12, 0, 0, 0);
                hint.VerticalAlignment = VerticalAlignment.Center;
                box.TextChanged += (_, _) =>
                {
                    hint.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
                    ShowPreview();
                };
                box.PreviewMouseLeftButtonDown += (_, _) =>
                {
                    Owner.SetHomeKeyboard(true);
                    box.Focus();
                };
                box.LostKeyboardFocus += (_, e) => ReleaseKeyboard(e);
                box.KeyDown += OnEntryKey;
            }

            var dueCell = new Grid { Margin = new Thickness(8, 0, 0, 0), Children = { _due, _dueHint } };
            var entry = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
            DockPanel.SetDock(dueCell, Dock.Right);
            entry.Children.Add(dueCell);
            entry.Children.Add(new Grid { Children = { _title, _titleHint } });
            _preview.Margin = new Thickness(12, 4, 0, 0);
            _preview.Visibility = Visibility.Collapsed;

            var dock = new DockPanel();
            DockPanel.SetDock(header, Dock.Top);
            DockPanel.SetDock(_preview, Dock.Bottom);
            DockPanel.SetDock(entry, Dock.Bottom);
            DockPanel.SetDock(_empty, Dock.Top);
            dock.Children.Add(header);
            dock.Children.Add(_preview);
            dock.Children.Add(entry);
            dock.Children.Add(_empty);
            dock.Children.Add(scroll);
            Border card = Card();
            card.Child = dock;
            return card;
        }

        public override void Refresh()
        {
            _signature = "";
            Render(DateTimeOffset.Now);
        }

        /// <summary>The times read in minutes, so the rows are only redrawn when one of them would read differently.</summary>
        public override void Tick(DateTime now) => Render(DateTimeOffset.Now);

        /// <summary>Starts typing a new deadline with the title (and when) filled in, e.g. from the palette.</summary>
        public void Prefill(string title, string? due)
        {
            _editing = null;
            _title.Text = title;
            _due.Text = due ?? "";
            Owner.SetHomeKeyboard(true);
            (string.IsNullOrEmpty(due) ? _due : _title).Focus();
        }

        private void Render(DateTimeOffset now)
        {
            IReadOnlyList<Deadline> all = Owner._deadlines.All();
            TimeSpan near = Owner.DeadlineNear;
            string signature = string.Join('|', all.Select(d =>
                $"{d.Key}:{d.Title}:{DeadlineTiming.Describe(d.Due - now)}:{Owner._deadlines.SnoozedUntil(d) is not null}")) + "#" + now.Date.Ticks;
            if (signature == _signature)
            {
                return;
            }

            _signature = signature;
            _rows.Children.Clear();
            foreach (Deadline deadline in all)
            {
                _rows.Children.Add(Row(deadline, now, near));
            }

            int overdue = all.Count(d => d.Due <= now);
            _count.Text = all.Count == 0 ? "" : overdue > 0 ? $"{all.Count} · {overdue} overdue" : $"{all.Count}";
            _empty.Visibility = all.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private FrameworkElement Row(Deadline deadline, DateTimeOffset now, TimeSpan near)
        {
            TimeSpan left = deadline.Due - now;
            DateTimeOffset? snoozed = Owner._deadlines.SnoozedUntil(deadline);
            Brush tint = ThemeManager.Brush(DeadlineTiming.Color(DeadlineTiming.Urgency(left, near)));

            var row = new Border { Height = RowHeight, Margin = new Thickness(-6, 0, -6, 0), Padding = new Thickness(6, 0, 2, 0), Background = Brushes.Transparent };
            row.SetResourceReference(Border.CornerRadiusProperty, "SmallRadius");

            var bar = new Border { Width = 3, Height = 22, CornerRadius = new CornerRadius(1.5), Background = tint, VerticalAlignment = VerticalAlignment.Center, Opacity = snoozed is null ? 1 : 0.4 };

            var title = new TextBlock { Text = deadline.Title, FontSize = 12.5, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = deadline.Title };
            string where = deadline.Source switch
            {
                DeadlineSource.Calendar => "Calendar",
                DeadlineSource.Plugin => deadline.Owner ?? "Plugin",
                DeadlineSource.Link => "Script",
                _ => "",
            };
            string[] parts = [DeadlineTiming.When(deadline.Due, now), deadline.Detail ?? "", where, snoozed is { } until ? $"snoozed until {until.ToLocalTime():t}" : ""];
            TextBlock detail = Secondary(string.Join(" · ", parts.Where(p => p.Length > 0)), 11);

            var lefts = new TextBlock { Text = DeadlineTiming.Describe(left), FontSize = 11.5, FontWeight = FontWeights.SemiBold, Foreground = tint, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 4, 0) };
            System.Windows.Documents.Typography.SetNumeralAlignment(lefts, FontNumeralAlignment.Tabular);

            Button done = RowButton("", "Done", () => Owner._deadlines.Complete(deadline.Id));
            Button snooze = RowButton("", "Snooze", () => { });
            snooze.Click += (_, _) => OpenSnoozeMenu(snooze, deadline, now);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Visibility = Visibility.Collapsed, VerticalAlignment = VerticalAlignment.Center, Children = { snooze, done } };

            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(9, 0, 0, 0), Children = { title, detail } };
            var dock = new DockPanel();
            DockPanel.SetDock(bar, Dock.Left);
            DockPanel.SetDock(buttons, Dock.Right);
            DockPanel.SetDock(lefts, Dock.Right);
            dock.Children.Add(bar);
            dock.Children.Add(buttons);
            dock.Children.Add(lefts);
            dock.Children.Add(text);
            row.Child = dock;

            row.MouseEnter += (_, _) =>
            {
                row.SetResourceReference(Border.BackgroundProperty, "HoverBrush");
                buttons.Visibility = Visibility.Visible;
            };
            row.MouseLeave += (_, _) =>
            {
                row.Background = Brushes.Transparent;
                buttons.Visibility = Visibility.Collapsed;
            };

            // A double-click on one of the user's own (or a script's) puts it in the boxes below to change it.
            if (deadline.Source is DeadlineSource.Manual or DeadlineSource.Link)
            {
                row.ToolTip = "Double-click to change";
                row.MouseLeftButtonDown += (_, e) =>
                {
                    if (e.ClickCount == 2)
                    {
                        e.Handled = true;
                        _editing = deadline.Id;
                        _title.Text = deadline.Title;
                        _due.Text = deadline.Due.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
                        Owner.SetHomeKeyboard(true);
                        _due.Focus();
                        _due.SelectAll();
                    }
                };
            }

            return row;
        }

        private static Button RowButton(string glyph, string tip, Action click)
        {
            var button = new Button { Content = glyph, Width = 24, Height = 24, FontSize = 11, ToolTip = tip };
            button.SetResourceReference(StyleProperty, "IconButton");
            button.Click += (_, e) =>
            {
                e.Handled = true;
                click();
            };
            return button;
        }

        private void OpenSnoozeMenu(Button anchor, Deadline deadline, DateTimeOffset now)
        {
            var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom };
            DateTimeOffset tomorrowMorning = new DateTimeOffset(now.Date.AddDays(1).AddHours(9), now.Offset);
            Add("For an hour", now.AddHours(1));
            Add("Until tomorrow 9:00", tomorrowMorning);
            Add("For a day", now.AddDays(1));
            if (Owner._deadlines.SnoozedUntil(deadline) is not null)
            {
                menu.Items.Add(new Separator());
                Add("Stop snoozing", now);
            }

            Owner._shelfMenuOpen = true;
            menu.Closed += (_, _) =>
            {
                Owner._shelfMenuOpen = false;
                if (!Owner.PointerOverIsland())
                {
                    Owner.ScheduleExpanded(false, CloseDelay);
                }
            };
            menu.IsOpen = true;

            void Add(string header, DateTimeOffset until)
            {
                var item = new MenuItem { Header = header };
                item.Click += (_, _) => Owner._deadlines.Snooze(deadline.Id, until);
                menu.Items.Add(item);
            }
        }

        private void ShowPreview()
        {
            if (_due.Text.Trim().Length == 0)
            {
                _preview.Visibility = _title.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
                _preview.Text = "Then when: tomorrow 17:00, fri 5pm, in 3d, 2026-12-01";
                _preview.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryTextBrush");
                return;
            }

            DateTimeOffset now = DateTimeOffset.Now;
            _preview.Visibility = Visibility.Visible;
            if (DueParser.TryParse(_due.Text, now, out DateTimeOffset due))
            {
                _preview.Text = $"Due {DeadlineTiming.When(due, now)}, {DeadlineTiming.Describe(due - now)}" + (_editing is null ? " · Enter adds it" : " · Enter saves it");
                _preview.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryTextBrush");
            }
            else
            {
                _preview.Text = "Not a time Notch can read. Try: tomorrow 17:00, fri 5pm, in 3d, 2026-12-01";
                _preview.Foreground = ThemeManager.Brush(GlowColor.Red);
            }
        }

        private void OnEntryKey(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                _editing = null;
                _title.Clear();
                _due.Clear();
                Keyboard.ClearFocus();
                Owner.SetHomeKeyboard(false);
                return;
            }

            if (e.Key != Key.Enter)
            {
                return;
            }

            e.Handled = true;
            if (_title.Text.Trim().Length == 0)
            {
                _title.Focus();
                return;
            }

            if (!DueParser.TryParse(_due.Text, DateTimeOffset.Now, out DateTimeOffset due))
            {
                _due.Focus();
                _due.BorderBrush = ThemeManager.Brush(GlowColor.Red);
                ShowPreview();
                return;
            }

            bool saved = _editing is { } id ? Owner._deadlines.Edit(id, _title.Text, due) : Owner._deadlines.Add(_title.Text, due) is not null;
            if (saved)
            {
                _editing = null;
                _title.Clear();
                _due.Clear();
                _due.ClearValue(Control.BorderBrushProperty);
                _title.Focus();
            }
        }
    }
}
