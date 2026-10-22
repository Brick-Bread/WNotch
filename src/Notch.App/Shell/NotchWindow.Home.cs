using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Notch.Core.Widgets;
using Notch.Platform.Display;

namespace Notch.App.Shell;

// The Home tab: the user's widgets in rows of one or two, and the edit mode that moves, sizes, sets up, adds and removes them.
public partial class NotchWindow
{
    private const string HomeEditGlyph = "";
    private const string HomeDoneGlyph = "";
    private const double HomeRowGap = 14;

    // Twice the height Home had before it could be changed, about; past this it scrolls.
    private const double MaxHomeHeight = 660;

    // How far the media card's colour wash reaches past the card; Home's scroller must not cut it off.
    private const double WashSpill = 66;

    private readonly Dictionary<string, HomeView> _homeViews = [];
    private readonly DispatcherTimer _homeTick = new() { Interval = TimeSpan.FromSeconds(1) };

    /// <summary>Which widgets the rows were last laid out with, so a change in what has content can tell whether they need laying out again.</summary>
    private string _homeShown = "";

    private bool _homeEditing;

    /// <summary>The widget a form is open for, and whether it was just added (and so goes again on Cancel).</summary>
    private (HomeWidget Widget, bool IsNew)? _homeForm;

    /// <summary>Reads each editor of the open form back into an option value.</summary>
    private readonly List<(string Key, Func<string> Read)> _homeFormFields = [];

    private bool _homePickerOpen;

    /// <summary>The widget being dragged to a new place, and where the press was.</summary>
    private (HomeView View, Point Start, bool Moving)? _homeDrag;

    /// <summary>Set while something on Home takes typing (a widget's form, a note); the notch then accepts the keyboard.</summary>
    private bool _homeKeyboard;

    private DateTime _resetArmedUntil;

    private List<HomeWidget> HomeWidgets => _settings.HomeWidgets;

    private bool HomeSheetOpen => _homeForm is not null || _homePickerOpen;

    private void InitializeHome()
    {
        _settings.HomeWidgets = HomeLayout.Normalize(_settings.HomeWidgets);

        HomeEditButton.Click += (_, _) => SetHomeEditing(!_homeEditing);
        HomeAdd.Click += (_, _) => OpenHomePicker();
        HomeReset.Click += (_, _) => ResetHome();
        HomeSheetCancel.Click += (_, _) => CloseHomeSheet(save: false);
        HomeSheetDone.Click += (_, _) => CloseHomeSheet(save: true);

        HomeRows.MouseMove += OnHomeDragMoved;
        HomeRows.MouseLeftButtonUp += (_, _) => EndHomeDrag();
        HomeRows.LostMouseCapture += (_, _) => EndHomeDrag();

        _homeTick.Tick += (_, _) => TickHome();
        LayoutHome();
    }

    /// <summary>The open island's height on the Home tab: its content measured at the width it has, plus the margins, up to the most it may grow to.</summary>
    private double HomeHeight()
    {
        const double width = 640 - 36;

        // A widget that changed (a row added, an item ticked) has only marked itself for measuring; its parents
        // learn of it in the next layout pass. Run that first, or the measure below would give the old height.
        UpdateLayout();
        HomePanel.Measure(new Size(width, double.PositiveInfinity));
        double tabs = Math.Max(24, TabStrip.DesiredSize.Height);
        double height = Math.Ceiling(14 + tabs + HomePanel.DesiredSize.Height + 18);

        double limit = MaxHomeHeight;
        if (_display is { } display)
        {
            limit = Math.Min(limit, (display.Bounds.Height / display.Scale) - _placement.EdgeGap - 40);
        }

        // The scroller reaches out over the tabs so the media card's wash is not cut off at Home's edge. Once it
        // scrolls it must not, or what scrolls up would show over the tabs.
        bool scrolls = height > limit;
        double spill = scrolls ? 0 : WashSpill;
        HomeScroll.Margin = new Thickness(-spill, -spill, -spill, 0);
        ((FrameworkElement)HomeScroll.Content).Margin = new Thickness(spill, spill, spill, 0);
        return Math.Min(height, limit);
    }

    /// <summary>Builds the rows again from the widget list: after the list changed, or a widget got or lost something to show.</summary>
    private void LayoutHome()
    {
        foreach (string gone in _homeViews.Keys.Except(HomeWidgets.Select(w => w.Id)).ToList())
        {
            _homeViews[gone].Release();
            _homeViews.Remove(gone);
        }

        foreach (Grid row in HomeRows.Children.OfType<Grid>())
        {
            row.Children.Clear();
        }

        HomeRows.Children.Clear();

        IReadOnlyList<IReadOnlyList<HomeWidget>> rows = HomeLayout.Rows(HomeWidgets, w => _homeEditing || IsHomeWidgetShown(ViewFor(w)));
        foreach (IReadOnlyList<HomeWidget> widgets in rows)
        {
            var row = new Grid { Margin = new Thickness(0, HomeRows.Children.Count == 0 ? 0 : HomeRowGap, 0, 0) };
            if (widgets[0].Width == WidgetWidth.Half)
            {
                row.ColumnDefinitions.Add(new ColumnDefinition());
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(HomeRowGap) });
                row.ColumnDefinitions.Add(new ColumnDefinition());
            }

            for (int i = 0; i < widgets.Count; i++)
            {
                HomeView view = ViewFor(widgets[i]);
                view.UpdateChrome(_homeEditing);
                Grid.SetColumn(view.Host, i * 2);
                row.Children.Add(view.Host);
            }

            HomeRows.Children.Add(row);
        }

        _homeShown = ShownSignature();
        HomeEmpty.Visibility = rows.Count == 0 && !_homeEditing ? Visibility.Visible : Visibility.Collapsed;
        HomeEditBar.Visibility = _homeEditing ? Visibility.Visible : Visibility.Collapsed;
        HomeEditBar.Margin = new Thickness(0, rows.Count == 0 ? 0 : HomeRowGap, 0, 0);
        UpdateStatsTimer();
        Refresh();
    }

    private HomeView ViewFor(HomeWidget widget)
    {
        if (!_homeViews.TryGetValue(widget.Id, out HomeView? view))
        {
            view = CreateHomeView(widget);
            _homeViews[widget.Id] = view;
            view.Refresh();
        }

        return view;
    }

    private static bool IsHomeWidgetShown(HomeView view) =>
        !view.IsEmpty || !view.Widget.HideWhenEmpty || WidgetCatalog.Find(view.Widget.Kind) is not { CanBeEmpty: true };

    private string ShownSignature() => string.Join(',', HomeWidgets.Where(w => _homeEditing || IsHomeWidgetShown(ViewFor(w))).Select(w => w.Id));

    /// <summary>
    /// Something a widget shows came or went (a song started, the last agent finished). Lays the rows out again if that
    /// hides or shows a widget, and in any case lets the island fit the new height.
    /// </summary>
    private void HomeContentChanged()
    {
        if (_homeViews.Count == 0)
        {
            return;
        }

        foreach (HomeView view in _homeViews.Values)
        {
            view.UpdateChrome(_homeEditing);
        }

        if (ShownSignature() != _homeShown)
        {
            LayoutHome();
        }
        else
        {
            Refresh();
        }
    }

    private IEnumerable<T> HomeViewsOf<T>() where T : HomeView => _homeViews.Values.OfType<T>();

    private void RefreshHomeViews()
    {
        foreach (HomeView view in _homeViews.Values)
        {
            view.Refresh();
        }
    }

    /// <summary>Clocks tick, and feeds and the weather fetch again when they are due, only while Home is on screen.</summary>
    private void UpdateHomeTick()
    {
        bool run = _expanded && _tab == NotchTab.Home;
        if (run && !_homeTick.IsEnabled)
        {
            _homeTick.Start();
            TickHome();
        }
        else if (!run)
        {
            _homeTick.Stop();
        }
    }

    private void TickHome()
    {
        DateTime now = DateTime.Now;
        foreach (HomeView view in _homeViews.Values.ToList())
        {
            view.Tick(now);
        }
    }

    private void SetHomeEditing(bool editing)
    {
        if (!editing)
        {
            CloseHomeSheet(save: false);
            EndHomeDrag();
        }

        if (_homeEditing == editing)
        {
            return;
        }

        _homeEditing = editing;
        HomeEditButton.Content = editing ? HomeDoneGlyph : HomeEditGlyph;
        HomeEditButton.ToolTip = editing ? "Done" : "Edit Home";
        HomeEditButton.SetResourceReference(ForegroundProperty, editing ? "TextBrush" : "SecondaryTextBrush");
        HomeReset.Content = "Reset";
        LayoutHome();
    }

    /// <summary>Leaves edit mode and whatever form is open, as closing the notch or leaving the tab does.</summary>
    private void LeaveHomeEditing()
    {
        SetHomeEditing(false);
        if (_homeKeyboard)
        {
            Keyboard.ClearFocus();
            SetHomeKeyboard(false);
        }
    }

    private void SaveHome() => _settingsStore.Save(_settings);

    /// <summary>The pencil and the settings button only belong to Home; other tabs keep the corner to themselves.</summary>
    private void UpdateHomeEditButton() =>
        HomeEditButton.Visibility = _tab == NotchTab.Home ? Visibility.Visible : Visibility.Collapsed;

    private void ResetHome()
    {
        // Resetting throws away notes and set-up widgets, so it takes a second click.
        if (DateTime.UtcNow > _resetArmedUntil)
        {
            _resetArmedUntil = DateTime.UtcNow.AddSeconds(4);
            HomeReset.Content = "Click again to reset";
            var disarm = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            disarm.Tick += (_, _) =>
            {
                disarm.Stop();
                HomeReset.Content = "Reset";
            };
            disarm.Start();
            return;
        }

        _resetArmedUntil = DateTime.MinValue;
        HomeReset.Content = "Reset";
        _settings.HomeWidgets = HomeLayout.Default();
        SaveHome();
        LayoutHome();
    }

    private void RemoveHomeWidget(HomeWidget widget)
    {
        HomeWidgets.Remove(widget);
        SaveHome();
        LayoutHome();
    }

    private void ToggleHomeWidth(HomeWidget widget)
    {
        widget.Width = widget.Width == WidgetWidth.Full ? WidgetWidth.Half : WidgetWidth.Full;
        SaveHome();
        LayoutHome();
    }

    private void OnHomeWidgetPressed(HomeView view, MouseButtonEventArgs e)
    {
        if (!_homeEditing || HomeSheetOpen)
        {
            return;
        }

        e.Handled = true;
        _homeDrag = (view, e.GetPosition(HomeRows), false);
        HomeRows.CaptureMouse();
    }

    private void OnHomeDragMoved(object sender, MouseEventArgs e)
    {
        if (_homeDrag is not null)
        {
            DragHomeWidget(e.GetPosition(HomeRows));
        }
    }

    /// <summary>Follows the pointer, at a point in <see cref="HomeRows"/>, with the widget being dragged.</summary>
    private void DragHomeWidget(Point point)
    {
        if (_homeDrag is not { } drag)
        {
            return;
        }

        if (!drag.Moving)
        {
            Vector moved = point - drag.Start;
            if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance
                && Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance)
            {
                return;
            }

            _homeDrag = drag with { Moving = true };
            drag.View.Host.Opacity = 0.5;
        }

        // Over another widget, the dragged one takes its place: before it in the top or left half, after it in the other.
        foreach (HomeView target in _homeViews.Values)
        {
            if (target == drag.View || target.Host.Parent is not Grid row || !HomeRows.Children.Contains(row))
            {
                continue;
            }

            Rect bounds = target.Host.TransformToAncestor(HomeRows).TransformBounds(new Rect(target.Host.RenderSize));
            if (!bounds.Contains(point))
            {
                continue;
            }

            bool sideBySide = row.ColumnDefinitions.Count > 0;
            bool after = sideBySide ? point.X > bounds.Left + (bounds.Width / 2) : point.Y > bounds.Top + (bounds.Height / 2);
            int from = HomeWidgets.IndexOf(drag.View.Widget);
            int to = HomeWidgets.IndexOf(target.Widget) + (after ? 1 : 0);
            if (from < to)
            {
                to--;
            }

            if (HomeLayout.Move(HomeWidgets, drag.View.Widget.Id, to))
            {
                // Laid out at once, so the next move is measured against where the widgets are now.
                LayoutHome();
                HomeRows.UpdateLayout();
            }

            break;
        }
    }

    private void EndHomeDrag()
    {
        if (_homeDrag is not { } drag)
        {
            return;
        }

        _homeDrag = null;
        drag.View.Host.Opacity = 1;
        if (HomeRows.IsMouseCaptured)
        {
            HomeRows.ReleaseMouseCapture();
        }

        if (drag.Moving)
        {
            SaveHome();
        }
    }

    /// <summary>The notch takes the keyboard while something on Home is being typed into, and gives it back after.</summary>
    private void SetHomeKeyboard(bool on)
    {
        if (_homeKeyboard == on)
        {
            return;
        }

        _homeKeyboard = on;
        if (on)
        {
            NoteForegroundWindow();
            UpdateKeyboardInteraction();
            Activate();
        }
        else
        {
            bool handBack = IsActive && _lastOtherWindow != 0;
            UpdateKeyboardInteraction();
            if (handBack)
            {
                OverlayWindow.SetForeground(_lastOtherWindow);
            }
        }
    }

    private void ShowHomeSheet(string title, bool canSave)
    {
        HomeSheetTitle.Text = title;
        HomeSheetDone.Visibility = canSave ? Visibility.Visible : Visibility.Collapsed;
        HomeSheetCancel.Content = canSave ? "Cancel" : "Close";
        HomeSheet.Visibility = Visibility.Visible;
        HomeScroll.Visibility = Visibility.Collapsed;
        Refresh();
    }

    private void CloseHomeSheet(bool save)
    {
        if (!HomeSheetOpen)
        {
            return;
        }

        if (_homeForm is { } form)
        {
            if (save)
            {
                foreach ((string key, Func<string> read) in _homeFormFields)
                {
                    if (key == nameof(HomeWidget.HideWhenEmpty))
                    {
                        form.Widget.HideWhenEmpty = read() == WidgetOption.On;
                    }
                    else
                    {
                        form.Widget.Options[key] = read().Trim();
                    }
                }

                ViewFor(form.Widget).OptionsChanged();
            }
            else if (form.IsNew)
            {
                HomeWidgets.Remove(form.Widget);
            }

            if (save || form.IsNew)
            {
                SaveHome();
            }
        }

        _homeForm = null;
        _homePickerOpen = false;
        _homeFormFields.Clear();
        HomeSheetBody.Children.Clear();
        HomeSheet.Visibility = Visibility.Collapsed;
        HomeScroll.Visibility = Visibility.Visible;
        Keyboard.ClearFocus();
        SetHomeKeyboard(false);
        LayoutHome();
    }

    /// <summary>Every kind of widget that can still be added, three to a row.</summary>
    private void OpenHomePicker()
    {
        CloseHomeSheet(save: false);
        _homePickerOpen = true;
        var grid = new UniformGrid { Columns = 3, Margin = new Thickness(-4, -4, -4, 0) };
        foreach (WidgetType type in HomeLayout.Addable(HomeWidgets))
        {
            var tile = new Border { Margin = new Thickness(4), Cursor = Cursors.Hand };
            tile.SetResourceReference(StyleProperty, "Card");
            tile.Padding = new Thickness(12, 9, 12, 9);
            tile.MouseEnter += (_, _) => tile.SetResourceReference(Border.BackgroundProperty, "HoverBrush");
            tile.MouseLeave += (_, _) => tile.SetResourceReference(Border.BackgroundProperty, "CardBrush");
            tile.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                AddHomeWidget(type);
            };

            var name = new DockPanel();
            var glyph = new TextBlock { Text = type.Glyph, FontSize = 13, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            glyph.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
            DockPanel.SetDock(glyph, Dock.Left);
            name.Children.Add(glyph);
            name.Children.Add(new TextBlock { Text = type.Name, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });

            var description = new TextBlock { Text = type.Description, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) };
            description.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryTextBrush");
            tile.Child = new StackPanel { Children = { name, description } };
            grid.Children.Add(tile);
        }

        HomeSheetBody.Children.Clear();
        HomeSheetBody.Children.Add(grid);
        ShowHomeSheet("Add a widget", canSave: false);
    }

    private void AddHomeWidget(WidgetType type)
    {
        HomeWidget widget = HomeWidget.Create(type);
        HomeWidgets.Add(widget);
        if (type.NeedsSetup)
        {
            // The form takes the picker's place.
            OpenHomeForm(widget, isNew: true);
            return;
        }

        // While the picker still counts as open, or closing the sheet would do nothing and leave it showing.
        CloseHomeSheet(save: false);
        SaveHome();
    }

    /// <summary>A form for a widget's options, opened from its gear in edit mode, or straight after adding one that needs setting up.</summary>
    private void OpenHomeForm(HomeWidget widget, bool isNew)
    {
        if (WidgetCatalog.Find(widget.Kind) is not { } type)
        {
            return;
        }

        HomeSheetBody.Children.Clear();
        _homeFormFields.Clear();
        _homePickerOpen = false;
        _homeForm = (widget, isNew);

        TextBox? first = null;
        foreach (WidgetOption option in type.Options)
        {
            IReadOnlyList<(string Value, string Label)> choices = option.Choices is { } fixedChoices
                ? [.. fixedChoices.Select(c => (c, c))]
                : PluginCardChoices();
            FrameworkElement editor = OptionEditor(option.Type, widget.Option(option.Key), choices, out Func<string> read, out TextBox? box);
            first ??= box;
            _homeFormFields.Add((option.Key, read));
            HomeSheetBody.Children.Add(FormRow(option.Label, option.Hint, editor));
        }

        if (type.CanBeEmpty)
        {
            string current = widget.HideWhenEmpty ? WidgetOption.On : WidgetOption.Off;
            FrameworkElement editor = OptionEditor(WidgetOptionType.Toggle, current, [], out Func<string> read, out _);
            _homeFormFields.Add((nameof(HomeWidget.HideWhenEmpty), read));
            HomeSheetBody.Children.Add(FormRow("Hide when there is nothing to show", null, editor));
        }

        if (HomeSheetBody.Children.Count == 0)
        {
            HomeSheetBody.Children.Add(Hint($"{type.Name} has nothing to set up here."));
        }

        ShowHomeSheet(isNew ? $"Add {type.Name.ToLowerInvariant()}" : $"{type.Name} settings", canSave: true);
        if (first is not null)
        {
            SetHomeKeyboard(true);
            first.Focus();
            first.CaretIndex = first.Text.Length;
        }
    }

    /// <summary>The plugin cards there are now, for a plugin card widget to show one of.</summary>
    private IReadOnlyList<(string Value, string Label)> PluginCardChoices() =>
        [.. _pluginCards.Snapshot().Select(entry => ($"{entry.PluginId}/{entry.Card.Id}", entry.Card.Label))];

    private FrameworkElement OptionEditor(
        WidgetOptionType kind,
        string value,
        IReadOnlyList<(string Value, string Label)> choices,
        out Func<string> read,
        out TextBox? box)
    {
        box = null;
        switch (kind)
        {
            case WidgetOptionType.Text:
            case WidgetOptionType.Lines:
            {
                var text = new TextBox { Text = value };
                text.SetResourceReference(StyleProperty, kind == WidgetOptionType.Lines ? "PillTextArea" : "PillTextBox");
                if (kind == WidgetOptionType.Lines)
                {
                    text.Height = 78;
                }
                else
                {
                    text.Padding = new Thickness(10, 5, 10, 5);
                }

                text.PreviewMouseLeftButtonDown += (_, _) => SetHomeKeyboard(true);
                text.KeyDown += (_, e) =>
                {
                    if (e.Key == Key.Escape)
                    {
                        e.Handled = true;
                        CloseHomeSheet(save: false);
                    }
                    else if (e.Key == Key.Enter && kind == WidgetOptionType.Text)
                    {
                        e.Handled = true;
                        CloseHomeSheet(save: true);
                    }
                };
                box = text;
                read = () => text.Text;
                return text;
            }

            case WidgetOptionType.Toggle:
            {
                bool on = value == WidgetOption.On;
                var toggle = new Button { HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0), MinWidth = 52 };
                toggle.SetResourceReference(StyleProperty, "PillButton");
                void Show()
                {
                    toggle.Content = on ? "On" : "Off";
                    toggle.SetResourceReference(BackgroundProperty, on ? "AccentHoverBrush" : "ControlBrush");
                }

                toggle.Click += (_, _) =>
                {
                    on = !on;
                    Show();
                };
                Show();
                read = () => on ? WidgetOption.On : WidgetOption.Off;
                return toggle;
            }

            default:
            {
                if (choices.Count == 0)
                {
                    read = () => value;
                    return Hint("No plugin has a card to show right now. Switch one on in Settings.");
                }

                string chosen = choices.Any(c => c.Value == value) ? value : choices[0].Value;
                var buttons = new WrapPanel { Margin = new Thickness(-6, -6, 0, 0) };
                foreach ((string choice, string label) in choices)
                {
                    var button = new Button { Content = label, Tag = choice, Margin = new Thickness(6, 6, 0, 0) };
                    button.SetResourceReference(StyleProperty, "PillButton");
                    button.Click += (_, _) =>
                    {
                        chosen = choice;
                        Mark();
                    };
                    buttons.Children.Add(button);
                }

                void Mark()
                {
                    foreach (Button button in buttons.Children.OfType<Button>())
                    {
                        button.SetResourceReference(BackgroundProperty, (string)button.Tag == chosen ? "AccentHoverBrush" : "ControlBrush");
                    }
                }

                Mark();
                read = () => chosen;
                return buttons;
            }
        }
    }

    private static FrameworkElement FormRow(string label, string? hint, FrameworkElement editor)
    {
        var row = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        var title = new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 4) };
        title.SetResourceReference(StyleProperty, "CardLabel");
        row.Children.Add(title);
        row.Children.Add(editor);
        if (hint is not null)
        {
            row.Children.Add(Hint(hint));
        }

        return row;
    }

    private static TextBlock Hint(string text)
    {
        var hint = new TextBlock { Text = text, FontSize = 11, Margin = new Thickness(2, 4, 0, 0), TextWrapping = TextWrapping.Wrap };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryTextBrush");
        return hint;
    }

    /// <summary>
    /// What a widget on Home is: its card, wrapped in a host that adds the empty state and, in edit mode, the
    /// handles for moving, sizing, setting up and removing it.
    /// </summary>
    private abstract class HomeView
    {
        private readonly Grid _host = new();
        private readonly Border _empty = new();
        private readonly TextBlock _emptyText = new() { TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        private readonly Grid _chrome = new() { Cursor = Cursors.SizeAll, Background = Brushes.Transparent };
        private readonly TextBlock _chromeNote = new() { FontSize = 11, Margin = new Thickness(0, 2, 0, 0), HorizontalAlignment = HorizontalAlignment.Center };
        private readonly Button _widthButton = new();
        private FrameworkElement? _content;

        protected HomeView(NotchWindow owner, HomeWidget widget)
        {
            Owner = owner;
            Widget = widget;
            Type = WidgetCatalog.Find(widget.Kind)!;

            _empty.SetResourceReference(StyleProperty, "Card");
            _empty.Child = _emptyText;
            _emptyText.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryTextBrush");
            _emptyText.FontSize = 12;

            // In edit mode the widget is a thing to pick up, not to use: the chrome covers it and takes the clicks.
            // The widget shows faintly through, so it can still be told apart from the others.
            _chrome.MouseLeftButtonDown += (_, e) => owner.OnHomeWidgetPressed(this, e);
            var backdrop = new Border { Opacity = 0.88 };
            backdrop.SetResourceReference(Border.BackgroundProperty, "IslandBrush");
            backdrop.SetResourceReference(Border.CornerRadiusProperty, "CardRadius");
            var outline = new Border { BorderThickness = new Thickness(1.5) };
            outline.SetResourceReference(Border.BorderBrushProperty, "TrackBrush");
            outline.SetResourceReference(Border.CornerRadiusProperty, "CardRadius");

            var name = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
            var glyph = new TextBlock { Text = Type.Glyph, FontSize = 16, HorizontalAlignment = HorizontalAlignment.Center };
            glyph.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
            name.Children.Add(glyph);
            name.Children.Add(new TextBlock { Text = Type.Name, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 4, 0, 0), HorizontalAlignment = HorizontalAlignment.Center });
            _chromeNote.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryTextBrush");
            name.Children.Add(_chromeNote);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 4, 4, 0) };
            buttons.Children.Add(ChromeButton(_widthButton, "", () => owner.ToggleHomeWidth(Widget)));
            if (Type.Options.Count > 0 || Type.CanBeEmpty)
            {
                buttons.Children.Add(ChromeButton(new Button(), "", () => owner.OpenHomeForm(Widget, isNew: false), "Settings"));
            }

            buttons.Children.Add(ChromeButton(new Button(), "", () => owner.RemoveHomeWidget(Widget), "Remove from Home"));
            _chrome.Children.Add(backdrop);
            _chrome.Children.Add(outline);
            _chrome.Children.Add(name);
            _chrome.Children.Add(buttons);
        }

        protected NotchWindow Owner { get; }

        public HomeWidget Widget { get; }

        public WidgetType Type { get; }

        /// <summary>What goes into the row.</summary>
        public FrameworkElement Host
        {
            get
            {
                if (_content is null)
                {
                    _content = CreateContent();
                    _host.MinHeight = MinHeight;
                    _host.Children.Add(_content);
                    _host.Children.Add(_empty);
                    _host.Children.Add(_chrome);
                }

                return _host;
            }
        }

        /// <summary>The widget has nothing to show; see <see cref="HomeWidget.HideWhenEmpty"/>.</summary>
        public virtual bool IsEmpty => false;

        /// <summary>Said in place of the widget while it is empty.</summary>
        protected virtual string EmptyText => "";

        protected virtual double MinHeight => 120;

        /// <summary>Builds the card once; built-in cards hand over the one from the XAML.</summary>
        protected abstract FrameworkElement CreateContent();

        /// <summary>Re-reads the options and repaints in the current theme.</summary>
        public virtual void Refresh()
        {
        }

        /// <summary>The user saved the widget's form.</summary>
        public virtual void OptionsChanged() => Refresh();

        /// <summary>Once a second while Home is on screen.</summary>
        public virtual void Tick(DateTime now)
        {
        }

        /// <summary>The widget left Home; a built-in card goes back where code can still reach it.</summary>
        public virtual void Release()
        {
            _host.Children.Clear();
            (_host.Parent as Panel)?.Children.Remove(_host);
        }

        public void UpdateChrome(bool editing)
        {
            bool empty = IsEmpty;
            _ = Host;
            _content!.Visibility = empty ? Visibility.Hidden : Visibility.Visible;
            _empty.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
            _emptyText.Text = EmptyText;
            _chromeNote.Text = empty && Widget.HideWhenEmpty && Type.CanBeEmpty ? "Hidden while empty" : "";
            _chromeNote.Visibility = _chromeNote.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            _chrome.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
            _widthButton.Content = Widget.Width == WidgetWidth.Full ? "" : "";
            _widthButton.ToolTip = Widget.Width == WidgetWidth.Full ? "Make half width" : "Make full width";
        }

        private static Button ChromeButton(Button button, string glyph, Action click, string? tip = null)
        {
            button.SetResourceReference(StyleProperty, "IconButton");
            button.Width = button.Height = 26;
            button.FontSize = 11;
            button.Content = glyph;
            button.ToolTip = tip;
            // A button handles its own press, so pressing one does not pick the widget up.
            button.Click += (_, e) =>
            {
                e.Handled = true;
                click();
            };
            return button;
        }

        protected static Border Card()
        {
            var card = new Border();
            card.SetResourceReference(StyleProperty, "Card");
            return card;
        }

        protected static TextBlock Label(string text)
        {
            var label = new TextBlock { Text = text, TextTrimming = TextTrimming.CharacterEllipsis };
            label.SetResourceReference(StyleProperty, "CardLabel");
            return label;
        }

        protected static TextBlock Secondary(string text = "", double size = 11)
        {
            var block = new TextBlock { Text = text, FontSize = size, TextTrimming = TextTrimming.CharacterEllipsis };
            block.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryTextBrush");
            return block;
        }

        /// <summary>
        /// Focus left one of this widget's text boxes. The notch gives the keyboard back unless it went to
        /// something else in the notch, such as another box, or a form is open.
        /// </summary>
        protected void ReleaseKeyboard(KeyboardFocusChangedEventArgs e)
        {
            if (!Owner.HomeSheetOpen && !(e.NewFocus is DependencyObject next && Owner.IsAncestorOf(next)))
            {
                Owner.SetHomeKeyboard(false);
            }
        }

        protected static Button Pill(string text)
        {
            var button = new Button { Content = text, Margin = new Thickness(6, 6, 0, 0) };
            button.SetResourceReference(StyleProperty, "PillButton");
            return button;
        }
    }

    /// <summary>One of the cards declared in the XAML, which the rest of the window keeps filling in wherever it is.</summary>
    private sealed class BuiltInView(NotchWindow owner, HomeWidget widget, FrameworkElement card, Func<bool> isEmpty, string emptyText, double minHeight)
        : HomeView(owner, widget)
    {
        public override bool IsEmpty => isEmpty();

        protected override string EmptyText => emptyText;

        protected override double MinHeight => minHeight;

        protected override FrameworkElement CreateContent()
        {
            (card.Parent as Panel)?.Children.Remove(card);
            return card;
        }

        public override void Release()
        {
            base.Release();
            card.Visibility = Visibility.Visible;
            Owner.HomeParts.Children.Add(card);
        }
    }
}
