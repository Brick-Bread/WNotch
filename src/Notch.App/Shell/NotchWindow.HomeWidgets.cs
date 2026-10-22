using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Notch.Core.Activities;
using Activity = Notch.Core.Activities.Activity;
using Notch.Core.Clipboard;
using Notch.Core.Plugins;
using Notch.Core.Terminal;
using Notch.Core.Widgets;
using Notch.Platform.Stats;

namespace Notch.App.Shell;

// The widgets Home can show besides its original four cards.
public partial class NotchWindow
{
    private const int MaxShelfPeek = 6;
    private const int MaxClipboardPeek = 3;

    private readonly WeatherService _weather = new(Http);

    private HomeView CreateHomeView(HomeWidget widget) => widget.Kind switch
    {
        WidgetKinds.Media => new BuiltInView(this, widget, MediaCard, () => _media.Current is null, "Nothing playing", 0),
        WidgetKinds.Agents => new BuiltInView(this, widget, AgentsCard, () => _agentRowsShown == 0, "No coding agents running", 0),
        WidgetKinds.Timer => new BuiltInView(this, widget, TimerCard, () => false, "", 150),
        WidgetKinds.Calendar => new BuiltInView(this, widget, CalendarCard, () => false, "", 150),
        WidgetKinds.Clock => new ClockView(this, widget),
        WidgetKinds.Stats => new StatsView(this, widget),
        WidgetKinds.Shelf => new ShelfView(this, widget),
        WidgetKinds.Clipboard => new ClipboardView(this, widget),
        WidgetKinds.Weather => new WeatherView(this, widget),
        WidgetKinds.Note => new NoteView(this, widget),
        WidgetKinds.Launcher => new ButtonsView(this, widget, runs: false),
        WidgetKinds.Command => new ButtonsView(this, widget, runs: true),
        WidgetKinds.Feed => new FeedView(this, widget),
        _ => new PluginCardView(this, widget),
    };

    /// <summary>Home has a stats widget on screen, which needs the sampler running as the Stats tab does.</summary>
    private bool HomeShowsStats => _tab == NotchTab.Home && HomeViewsOf<StatsView>().Any();

    private void ShowHomeStats(SystemStats stats)
    {
        foreach (StatsView view in HomeViewsOf<StatsView>())
        {
            view.Show(stats);
        }
    }

    private void Notice(string id, string title, string? detail = null, GlowColor? color = null) => _activities.Publish(new Activity
    {
        Id = id,
        Tier = ActivityTier.Transient,
        Title = title,
        Detail = detail,
        Glow = color is { } c ? new Glow(c, GlowPattern.Flash) : null,
        Lifetime = TimeSpan.FromSeconds(3),
    });

    private sealed class ClockView(NotchWindow owner, HomeWidget widget) : HomeView(owner, widget)
    {
        private readonly TextBlock _label = Label("");
        private readonly TextBlock _time = new() { FontSize = 34, FontWeight = FontWeights.Light, Typography = { NumeralAlignment = FontNumeralAlignment.Tabular } };
        private readonly TextBlock _date = Secondary(size: 12);
        private TimeZoneInfo? _zone;
        private bool _unknownZone;

        protected override FrameworkElement CreateContent()
        {
            _time.SetResourceReference(TextBlock.FontFamilyProperty, "DisplayFontFamily");
            var stack = new DockPanel();
            DockPanel.SetDock(_label, Dock.Top);
            DockPanel.SetDock(_date, Dock.Bottom);
            stack.Children.Add(_label);
            stack.Children.Add(_date);
            stack.Children.Add(new Viewbox { Child = _time, StretchDirection = StretchDirection.DownOnly, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center });
            Border card = Card();
            card.Child = stack;
            return card;
        }

        public override void Refresh()
        {
            string zone = Widget.Option("zone");
            _zone = ClockZones.Find(zone);
            _unknownZone = zone.Trim().Length > 0 && _zone is null;
            string title = Widget.Option(WidgetCatalog.TitleKey);
            _label.Text = title.Length > 0 ? title : zone.Trim().Length > 0 ? zone.Trim() : "Local time";
            Tick(DateTime.Now);
        }

        public override void Tick(DateTime now)
        {
            DateTime here = _zone is null ? now : TimeZoneInfo.ConvertTime(now, _zone);
            bool seconds = Widget.Flag("seconds");
            _time.Text = Widget.Flag("hours24")
                ? here.ToString(seconds ? "HH:mm:ss" : "HH:mm")
                : here.ToString(seconds ? "T" : "t");

            string date = here.ToString("dddd, d MMMM");
            if (_unknownZone)
            {
                date = "Time zone not found; showing this computer's";
            }
            else if (_zone is not null && here.Date != now.Date)
            {
                date += here.Date > now.Date ? " · tomorrow" : " · yesterday";
            }

            _date.Text = date;
        }
    }

    private sealed class StatsView(NotchWindow owner, HomeWidget widget) : HomeView(owner, widget)
    {
        private readonly StackPanel _rows = new();
        private readonly Dictionary<string, (TextBlock Value, Sparkline? Chart)> _metrics = [];

        protected override FrameworkElement CreateContent()
        {
            var dock = new DockPanel();
            TextBlock label = Label("System");
            DockPanel.SetDock(label, Dock.Top);
            dock.Children.Add(label);
            dock.Children.Add(_rows);
            Border card = Card();
            card.Child = dock;
            return card;
        }

        public override void Refresh()
        {
            _rows.Children.Clear();
            _metrics.Clear();
            (string Key, string Name, GlowColor Color, bool Chart)[] all =
            [
                ("cpu", "CPU", GlowColor.Blue, true),
                ("memory", "Memory", GlowColor.Violet, true),
                ("gpu", "GPU", GlowColor.Green, true),
                ("network", "Down / up", GlowColor.Cyan, false),
                ("battery", "Battery", GlowColor.Green, false),
            ];
            var chosen = all.Where(m => Widget.Flag(m.Key)).ToList();

            // With few metrics each gets a chart under it; with more, one line each is all that fits.
            bool charts = chosen.Count <= 2;
            foreach ((string key, string name, GlowColor color, bool chart) in chosen)
            {
                var line = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
                var value = new TextBlock { Text = "–", FontWeight = FontWeights.SemiBold, Foreground = ThemeManager.Brush(color), Typography = { NumeralAlignment = FontNumeralAlignment.Tabular } };
                DockPanel.SetDock(value, Dock.Right);
                line.Children.Add(value);
                line.Children.Add(Secondary(name, 12));
                _rows.Children.Add(line);

                Sparkline? sparkline = null;
                if (charts && chart)
                {
                    sparkline = new Sparkline { Height = 18, Margin = new Thickness(0, 3, 0, 0), Stroke = ThemeManager.Brush(color) };
                    _rows.Children.Add(sparkline);
                }

                _metrics[key] = (value, sparkline);
            }

            if (chosen.Count == 0)
            {
                _rows.Children.Add(Secondary("Pick what to show with the gear in edit mode.", 12));
            }
        }

        public void Show(SystemStats stats)
        {
            Set("cpu", StatsFormat.Percent(stats.CpuUsage), stats.CpuUsage);
            Set("memory", StatsFormat.Percent(stats.MemoryUsage), stats.MemoryUsage);
            Set("gpu", stats.GpuUsage is { } gpu ? StatsFormat.Percent(gpu) : "n/a", stats.GpuUsage ?? 0);
            Set("network", $"{StatsFormat.Rate(stats.DownloadBytesPerSecond)} / {StatsFormat.Rate(stats.UploadBytesPerSecond)}", 0);
            Set("battery", stats.BatteryPercent is { } battery ? $"{battery}%" : "None", 0);

            void Set(string key, string text, double value)
            {
                if (_metrics.TryGetValue(key, out (TextBlock Value, Sparkline? Chart) metric))
                {
                    metric.Value.Text = text;
                    metric.Chart?.Push(value);
                }
            }
        }
    }

    private sealed class ShelfView(NotchWindow owner, HomeWidget widget) : HomeView(owner, widget)
    {
        private readonly StackPanel _tiles = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(-4, 6, -4, 0) };
        private readonly TextBlock _label = Label("Shelf");

        public override bool IsEmpty => Owner._shelfTiles.Count == 0;

        protected override string EmptyText => "Nothing on the shelf";

        protected override double MinHeight => 0;

        protected override FrameworkElement CreateContent()
        {
            var header = new DockPanel();
            Button open = Pill("Open shelf");
            open.FontSize = 11;
            open.Padding = new Thickness(9, 2, 9, 2);
            open.Margin = new Thickness(0, -3, -6, -3);
            open.Click += (_, _) => Owner.TabShelf.IsChecked = true;
            DockPanel.SetDock(open, Dock.Right);
            header.Children.Add(open);
            header.Children.Add(_label);

            var dock = new DockPanel();
            DockPanel.SetDock(header, Dock.Top);
            dock.Children.Add(header);
            dock.Children.Add(_tiles);
            Border card = Card();
            card.Child = dock;
            return card;
        }

        public override void Refresh()
        {
            IReadOnlyList<ShelfTile> tiles = Owner._shelfTiles;
            _label.Text = tiles.Count switch
            {
                0 => "Shelf",
                1 => "Shelf · 1 item",
                _ => $"Shelf · {tiles.Count} items",
            };

            _tiles.Children.Clear();
            foreach (ShelfTile tile in tiles.Take(MaxShelfPeek))
            {
                _tiles.Children.Add(Tile(tile));
            }
        }

        // The same handlers as the Shelf tab's tiles: double-click opens, drag takes the file out, right-click has the menu.
        private FrameworkElement Tile(ShelfTile tile)
        {
            var border = new Border { Width = 82, Height = 70, Margin = new Thickness(4), Padding = new Thickness(4, 6, 4, 4), Cursor = Cursors.Hand, Tag = tile, ToolTip = tile.Path, Background = Brushes.Transparent };
            border.SetResourceReference(Border.CornerRadiusProperty, "ControlRadius");
            border.MouseEnter += (_, _) => border.SetResourceReference(Border.BackgroundProperty, "HoverBrush");
            border.MouseLeave += (_, _) => border.Background = Brushes.Transparent;
            border.MouseLeftButtonDown += Owner.OnShelfTilePressed;
            border.MouseMove += Owner.OnShelfTileMoved;
            border.MouseRightButtonUp += Owner.OnShelfTileMenu;

            var glyph = new TextBlock { Text = tile.Glyph, FontSize = 24, Opacity = 0.5, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            glyph.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
            glyph.SetBinding(VisibilityProperty, new Binding(nameof(ShelfTile.GlyphVisibility)) { Source = tile });
            var picture = new Image { Width = 34, Height = 34, Stretch = Stretch.Uniform };
            picture.SetBinding(Image.SourceProperty, new Binding(nameof(ShelfTile.Picture)) { Source = tile });

            var name = new TextBlock { Text = tile.Name, FontSize = 11, TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            var dock = new DockPanel();
            DockPanel.SetDock(name, Dock.Bottom);
            dock.Children.Add(name);
            dock.Children.Add(new Grid { Children = { glyph, picture } });
            border.Child = dock;
            return border;
        }
    }

    private sealed class ClipboardView(NotchWindow owner, HomeWidget widget) : HomeView(owner, widget)
    {
        private readonly StackPanel _rows = new() { Margin = new Thickness(0, 4, 0, 0) };

        private bool HistoryOn => Owner._settings.ClipboardHistory || Owner._clipboardDemo;

        public override bool IsEmpty => !HistoryOn || Owner._clipboard.Snapshot().Count == 0;

        protected override string EmptyText => HistoryOn ? "Nothing copied yet" : "Clipboard history is off in Settings";

        protected override FrameworkElement CreateContent()
        {
            var dock = new DockPanel();
            TextBlock label = Label("Clipboard");
            DockPanel.SetDock(label, Dock.Top);
            dock.Children.Add(label);
            dock.Children.Add(_rows);
            Border card = Card();
            card.Child = dock;
            return card;
        }

        public override void Refresh()
        {
            _rows.Children.Clear();
            foreach (ClipboardItem item in Owner._clipboard.Snapshot().Take(MaxClipboardPeek))
            {
                var row = new ClipboardRow(item);
                var line = new Border { Padding = new Thickness(6, 3, 6, 3), Margin = new Thickness(-6, 0, -6, 0), Cursor = Cursors.Hand, Background = Brushes.Transparent, ToolTip = "Click to copy it again" };
                line.SetResourceReference(Border.CornerRadiusProperty, "SmallRadius");
                line.MouseEnter += (_, _) => line.SetResourceReference(Border.BackgroundProperty, "HoverBrush");
                line.MouseLeave += (_, _) => line.Background = Brushes.Transparent;
                line.MouseLeftButtonDown += (_, e) =>
                {
                    e.Handled = true;
                    Owner.CopyAgain(item);
                };
                line.Child = new StackPanel
                {
                    Children =
                    {
                        new TextBlock { Text = row.Preview, FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis },
                        Secondary(row.Subtitle, 10.5),
                    },
                };
                _rows.Children.Add(line);
            }
        }
    }

    private sealed class WeatherView(NotchWindow owner, HomeWidget widget) : HomeView(owner, widget)
    {
        private static readonly TimeSpan Every = TimeSpan.FromMinutes(30);
        private static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(2);

        private readonly TextBlock _label = Label("Weather");
        private readonly TextBlock _temperature = new() { FontSize = 34, FontWeight = FontWeights.Light };
        private readonly TextBlock _glyph = new() { FontSize = 26, Opacity = 0.8, VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock _summary = new() { FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis };
        private readonly TextBlock _detail = Secondary(size: 11);
        private DateTime _due = DateTime.MinValue;
        private bool _fetching;

        protected override FrameworkElement CreateContent()
        {
            _temperature.SetResourceReference(TextBlock.FontFamilyProperty, "DisplayFontFamily");
            _glyph.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");

            var middle = new DockPanel();
            DockPanel.SetDock(_glyph, Dock.Right);
            middle.Children.Add(_glyph);
            middle.Children.Add(_temperature);

            var dock = new DockPanel();
            DockPanel.SetDock(_label, Dock.Top);
            DockPanel.SetDock(_detail, Dock.Bottom);
            DockPanel.SetDock(_summary, Dock.Bottom);
            dock.Children.Add(_label);
            dock.Children.Add(_detail);
            dock.Children.Add(_summary);
            dock.Children.Add(middle);
            Border card = Card();
            card.Child = dock;
            return card;
        }

        public override void OptionsChanged()
        {
            _due = DateTime.MinValue;
            Refresh();
            Tick(DateTime.Now);
        }

        public override void Refresh()
        {
            if (Widget.Option("city").Trim().Length == 0)
            {
                _label.Text = "Weather";
                _temperature.Text = "–";
                _glyph.Text = "";
                _summary.Text = "Set a city with the gear in edit mode";
                _detail.Text = "";
            }
        }

        public override async void Tick(DateTime now)
        {
            string city = Widget.Option("city").Trim();
            if (city.Length == 0 || _fetching || now < _due)
            {
                return;
            }

            _fetching = true;
            bool fahrenheit = Widget.Option("units") == "Fahrenheit";
            try
            {
                (WeatherPlace place, WeatherReport report) = await Owner._weather.FetchAsync(city, fahrenheit);
                _label.Text = place.Name;
                _temperature.Text = $"{Math.Round(report.Temperature)}°";
                _glyph.Text = OpenMeteo.IsClear(report.Code) ? (report.IsDay ? "" : "") : "";
                _summary.Text = OpenMeteo.Describe(report.Code);
                _detail.Text = $"H {Math.Round(report.High)}°  L {Math.Round(report.Low)}°  ·  wind {Math.Round(report.WindSpeed)} {(fahrenheit ? "mph" : "km/h")}";
                _due = now + Every;
            }
            catch (Exception e) when (e is HttpRequestException or FormatException or TaskCanceledException)
            {
                _label.Text = city;
                _summary.Text = e is FormatException ? e.Message : "Could not reach Open-Meteo";
                _detail.Text = "Trying again in a few minutes";
                _due = now + RetryAfter;
            }
            finally
            {
                _fetching = false;
            }
        }
    }

    private sealed class NoteView(NotchWindow owner, HomeWidget widget) : HomeView(owner, widget)
    {
        private readonly TextBlock _label = Label("Note");
        private readonly TextBox _text = new()
        {
            AcceptsReturn = true,
            AcceptsTab = false,
            TextWrapping = TextWrapping.Wrap,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Padding = new Thickness(0),
            Margin = new Thickness(-2, 6, 0, 0),
            FontSize = 12.5,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
        };

        private readonly DispatcherTimer _save = new() { Interval = TimeSpan.FromMilliseconds(800) };

        protected override FrameworkElement CreateContent()
        {
            _text.SetResourceReference(Control.ForegroundProperty, "TextBrush");
            _text.SetResourceReference(TextBoxBase.CaretBrushProperty, "TextBrush");
            _text.SetResourceReference(TextBoxBase.SelectionBrushProperty, "AccentBrush");
            _text.Text = Widget.Option(WidgetCatalog.NoteTextKey);

            // The notch does not take the keyboard by itself; clicking into the note asks for it.
            _text.PreviewMouseLeftButtonDown += (_, _) =>
            {
                Owner.SetHomeKeyboard(true);
                _text.Focus();
            };
            _text.TextChanged += (_, _) =>
            {
                _save.Stop();
                _save.Start();
            };
            _text.LostKeyboardFocus += (_, _) =>
            {
                Save();
                if (!Owner.HomeSheetOpen)
                {
                    Owner.SetHomeKeyboard(false);
                }
            };
            _text.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Escape)
                {
                    e.Handled = true;
                    Keyboard.ClearFocus();
                    Owner.SetHomeKeyboard(false);
                }
            };
            _save.Tick += (_, _) => Save();

            var dock = new DockPanel();
            DockPanel.SetDock(_label, Dock.Top);
            dock.Children.Add(_label);
            dock.Children.Add(_text);
            Border card = Card();
            card.Child = dock;
            return card;
        }

        public override void Refresh() => _label.Text = Widget.Option(WidgetCatalog.TitleKey) is { Length: > 0 } title ? title : "Note";

        public override void Release()
        {
            Save();
            base.Release();
        }

        private void Save()
        {
            _save.Stop();
            if (Widget.Option(WidgetCatalog.NoteTextKey) != _text.Text)
            {
                Widget.Options[WidgetCatalog.NoteTextKey] = _text.Text;
                Owner.SaveHome();
            }
        }
    }

    /// <summary>A launcher, whose buttons open things, or a commands widget, whose buttons run them.</summary>
    private sealed class ButtonsView(NotchWindow owner, HomeWidget widget, bool runs) : HomeView(owner, widget)
    {
        private readonly TextBlock _label = Label("");
        private readonly WrapPanel _buttons = new() { Margin = new Thickness(-6, 2, 0, 0) };

        protected override FrameworkElement CreateContent()
        {
            var dock = new DockPanel();
            DockPanel.SetDock(_label, Dock.Top);
            dock.Children.Add(_label);
            dock.Children.Add(_buttons);
            Border card = Card();
            card.Child = dock;
            return card;
        }

        public override void Refresh()
        {
            _label.Text = Widget.Option(WidgetCatalog.TitleKey) is { Length: > 0 } title ? title : Type.Name;
            _buttons.Children.Clear();
            IReadOnlyList<WidgetButton> buttons = WidgetButtons.Parse(Widget.Option(runs ? "commands" : "items"));
            foreach (WidgetButton item in buttons)
            {
                Button button = Pill(item.Label);
                button.ToolTip = item.Target;
                button.Click += (_, _) =>
                {
                    if (runs)
                    {
                        Run(button, item);
                    }
                    else
                    {
                        Open(item);
                    }
                };
                _buttons.Children.Add(button);
            }

            if (buttons.Count == 0)
            {
                TextBlock hint = Secondary("Add buttons with the gear in edit mode.", 12);
                hint.Margin = new Thickness(6, 8, 0, 0);
                _buttons.Children.Add(hint);
            }
        }

        private void Open(WidgetButton item)
        {
            try
            {
                Process.Start(new ProcessStartInfo(Environment.ExpandEnvironmentVariables(item.Target.Trim('"'))) { UseShellExecute = true })?.Dispose();
            }
            catch (Exception e) when (e is Win32Exception or InvalidOperationException or System.IO.FileNotFoundException)
            {
                Owner.Notice("notice.launcher", $"Could not open {item.Label}", item.Target, GlowColor.Red);
            }
        }

        private async void Run(Button button, WidgetButton item)
        {
            if (Widget.Option("run") == WidgetCatalog.RunInTerminal)
            {
                if (TerminalPresets.TryParse($"{item.Label} = {item.Target}", [], out TerminalProfile profile))
                {
                    Owner.OpenTerminal(profile);
                }

                return;
            }

            // Through cmd, so built-in commands, pipes and && work as they do at a prompt.
            var start = new ProcessStartInfo("cmd.exe", $"/d /s /c \"{item.Target}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Owner._terminalFolder,
            };

            button.IsEnabled = false;
            try
            {
                using Process process = Process.Start(start) ?? throw new InvalidOperationException();
                await process.WaitForExitAsync();
                bool ok = process.ExitCode == 0;
                Owner.Notice("notice.command", ok ? $"{item.Label} finished" : $"{item.Label} failed", ok ? null : $"Exit code {process.ExitCode}", ok ? GlowColor.Green : GlowColor.Red);
            }
            catch (Exception e) when (e is Win32Exception or InvalidOperationException)
            {
                Owner.Notice("notice.command", $"Could not run {item.Label}", item.Target, GlowColor.Red);
            }
            finally
            {
                button.IsEnabled = true;
            }
        }
    }

    private sealed class FeedView(NotchWindow owner, HomeWidget widget) : HomeView(owner, widget)
    {
        private readonly TextBlock _label = Label("Feed");
        private readonly TextBlock _text = new() { FontSize = 12.5, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 6, 0, 0) };
        private readonly TextBlock _updated = Secondary(size: 10.5);
        private DateTime _due = DateTime.MinValue;
        private bool _fetching;

        protected override FrameworkElement CreateContent()
        {
            var dock = new DockPanel();
            DockPanel.SetDock(_label, Dock.Top);
            DockPanel.SetDock(_updated, Dock.Bottom);
            dock.Children.Add(_label);
            dock.Children.Add(_updated);
            dock.Children.Add(_text);
            Border card = Card();
            card.Child = dock;
            return card;
        }

        private TimeSpan Every => TimeSpan.FromMinutes(int.TryParse(Widget.Option("minutes").Split(' ')[0], out int minutes) ? minutes : 15);

        public override void OptionsChanged()
        {
            _due = DateTime.MinValue;
            Refresh();
            Tick(DateTime.Now);
        }

        public override void Refresh()
        {
            _label.Text = Widget.Option(WidgetCatalog.TitleKey) is { Length: > 0 } title ? title : "Feed";
            if (Widget.Option("url").Trim().Length == 0)
            {
                _text.Text = "Set a link with the gear in edit mode.";
                _updated.Text = "";
            }
        }

        public override async void Tick(DateTime now)
        {
            string url = Widget.Option("url").Trim();
            if (url.Length == 0 || _fetching || now < _due)
            {
                return;
            }

            _fetching = true;
            try
            {
                // Some services, GitHub's API among them, turn away requests that do not say what sent them.
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.UserAgent.ParseAdd("Notch");
                using HttpResponseMessage response = await Http.SendAsync(request);
                response.EnsureSuccessStatusCode();
                string body = await response.Content.ReadAsStringAsync();
                string text = FeedText.Extract(body, Widget.Option("field"));
                _text.Text = text.Length > 0 ? text : "(empty)";
                _updated.Text = $"Updated {now:t}";
                _due = now + Every;
            }
            catch (Exception e) when (e is HttpRequestException or FormatException or UriFormatException or InvalidOperationException or TaskCanceledException)
            {
                _text.Text = e is FormatException ? e.Message : "Could not load the link.";
                _updated.Text = "Trying again in a minute";
                _due = now + TimeSpan.FromMinutes(1);
            }
            finally
            {
                _fetching = false;
            }
        }
    }

    private sealed class PluginCardView(NotchWindow owner, HomeWidget widget) : HomeView(owner, widget)
    {
        private readonly TextBlock _label = Label("");
        private readonly TextBlock _value = new() { TextTrimming = TextTrimming.CharacterEllipsis };
        private readonly TextBlock _detail = new() { FontSize = 11, Opacity = 0.55, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis };
        private readonly ProgressBar _progress = new() { Height = 4, Maximum = 1, BorderThickness = new Thickness(0) };
        private Border? _card;
        private PluginCard? _shown;

        public override bool IsEmpty => Find() is null;

        protected override string EmptyText => Widget.Option("card").Length == 0 ? "Pick a card with the gear" : "That plugin card is not there right now";

        protected override double MinHeight => 95;

        private PluginCard? Find()
        {
            string key = Widget.Option("card");
            return Owner._pluginCards.Snapshot().FirstOrDefault(e => $"{e.PluginId}/{e.Card.Id}" == key)?.Card;
        }

        protected override FrameworkElement CreateContent()
        {
            _value.SetResourceReference(StyleProperty, "CardValue");
            _progress.SetResourceReference(Control.BackgroundProperty, "TrackBrush");
            var dock = new DockPanel();
            DockPanel.SetDock(_label, Dock.Top);
            DockPanel.SetDock(_progress, Dock.Bottom);
            DockPanel.SetDock(_value, Dock.Top);
            dock.Children.Add(_label);
            dock.Children.Add(_progress);
            dock.Children.Add(_value);
            dock.Children.Add(_detail);
            _card = Card();
            _card.Child = dock;
            _card.MouseLeftButtonUp += (_, _) => _shown?.Clicked?.Invoke();
            return _card;
        }

        public override void Refresh()
        {
            _shown = Find();
            if (_shown is not { } card || _card is null)
            {
                return;
            }

            _label.Text = card.Label;
            _value.Text = card.Value;
            _detail.Text = card.Detail;
            if (card.Color is { } color)
            {
                _value.Foreground = ThemeManager.Brush(color);
                _progress.Foreground = ThemeManager.Brush(color);
            }
            else
            {
                _value.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
                _progress.SetResourceReference(Control.ForegroundProperty, "AccentBrush");
            }

            _progress.Value = Math.Clamp(card.Progress ?? 0, 0, 1);
            _progress.Visibility = card.Progress is null ? Visibility.Collapsed : Visibility.Visible;
            _card.Cursor = card.Clicked is null ? Cursors.Arrow : Cursors.Hand;
        }
    }
}
