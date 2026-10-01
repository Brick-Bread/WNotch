using System.Media;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Notch.Core.Activities;
using Notch.Core.Widgets;
using Notch.Platform.Display;
using Notch.Platform.Stats;

namespace Notch.App.Shell;

// The timer and calendar cards on Home, and the Stats tab.
public partial class NotchWindow
{
    private const string TimerActivityId = "timer";
    private const string PomodoroNoticeId = "timer.phase";
    private const string TimerGlyph = "";
    private const string TimerEntryHint = "Timer: 12, 1:30, 90s, 1h20m";
    private const int MaxCalendarEntries = 4;

    // Each calendar feed gets the next of these for its dot, starting over after the last.
    private static readonly GlowColor[] FeedColors =
        [GlowColor.Blue, GlowColor.Violet, GlowColor.Green, GlowColor.Orange, GlowColor.Cyan, GlowColor.Red, GlowColor.Yellow];

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    private readonly CountdownTimer _countdown = new();
    private readonly DispatcherTimer _countdownTick = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly CalendarService _calendar = new(Http);
    private readonly DispatcherTimer _calendarTimer = new() { Interval = TimeSpan.FromMinutes(15) };
    private readonly SystemStatsSampler _stats = new();
    private readonly DispatcherTimer _statsTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    /// <summary>Set while the timer runs a Pomodoro cycle rather than a single countdown.</summary>
    private PomodoroCycle? _pomodoro;

    /// <summary>Name of the preset that is running, when it has one; shown in place of "Timer".</summary>
    private string? _timerName;

    /// <summary>Set while the box for typing a timer length is showing.</summary>
    private bool _timerEntryOpen;

    /// <summary>The last window other than the notch that had the keyboard, to hand it back to after typing.</summary>
    private nint _lastOtherWindow;
    private string? _lastTimerDisplay;
    private bool _timerAnnounced;
    private bool _statsSampling;
    private int? _batteryPercent;

    /// <summary>Reloads the calendar feeds, e.g. after they were edited in settings.</summary>
    public void RefreshCalendar() => _ = RefreshCalendarAsync();

    private void InitializeWidgets()
    {
        _countdownTick.Tick += (_, _) => UpdateTimer();
        TimerPauseResume.Click += (_, _) =>
        {
            if (_countdown.State == CountdownState.Paused)
            {
                _countdown.Resume();
            }
            else
            {
                _countdown.Pause();
            }

            UpdateTimer();
        };
        TimerReset.Click += (_, _) =>
        {
            _pomodoro = null;
            _timerName = null;
            _countdown.Reset();
            UpdateTimer();
        };
        TimerPomodoro.Click += (_, _) =>
        {
            _pomodoro = new PomodoroCycle(_settings.PomodoroDurations());
            _timerName = null;
            StartCountdown(_pomodoro.CurrentDuration);
        };

        TimerCustom.Click += (_, _) => SetTimerEntryOpen(true);
        TimerEntryStart.Click += (_, _) => StartTypedTimer();
        TimerEntryCancel.Click += (_, _) => SetTimerEntryOpen(false);
        TimerInput.TextChanged += (_, _) => TimerInput.ClearValue(BorderBrushProperty);
        TimerInput.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                StartTypedTimer();
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                SetTimerEntryOpen(false);
            }
        };
        ShowTimerPresets();

        _calendar.Changed += (_, _) => Dispatcher.BeginInvoke(UpdateCalendar);
        _calendarTimer.Tick += (_, _) => RefreshCalendar();
        _calendarTimer.Start();
        UpdateCalendar();
        RefreshCalendar();

        _statsTimer.Tick += (_, _) => SampleStats();
    }

    /// <summary>Colours the widgets whose colour is set in code. Runs again whenever the theme changes.</summary>
    private void ApplyWidgetColors()
    {
        Tint(StatsCpu, StatsCpuChart, GlowColor.Blue);
        Tint(StatsMemory, StatsMemoryChart, GlowColor.Violet);
        Tint(StatsGpu, StatsGpuChart, GlowColor.Green);
        Tint(StatsDownload, null, GlowColor.Cyan);
        Tint(StatsUpload, null, GlowColor.Orange);
        UpdateBatteryColor();

        // Forget what was last shown so the timer repaints in the new theme's shade.
        _lastTimerDisplay = null;
        UpdateTimer();
        UpdateCalendar();

        static void Tint(TextBlock value, Sparkline? chart, GlowColor color)
        {
            Brush brush = ThemeManager.Brush(color);
            value.Foreground = brush;
            chart?.Stroke = brush;
        }
    }

    /// <summary>Green with plenty of charge, amber below half, red when low; plain when there is no battery.</summary>
    private void UpdateBatteryColor()
    {
        if (_batteryPercent is { } percent)
        {
            StatsBattery.Foreground = ThemeManager.Brush(percent <= 20 ? GlowColor.Red : percent <= 50 ? GlowColor.Amber : GlowColor.Green);
        }
        else
        {
            StatsBattery.ClearValue(TextBlock.ForegroundProperty);
        }
    }

    /// <summary>What the timer is counting: red for Pomodoro focus (the tomato), green for breaks, orange for a plain timer.</summary>
    private GlowColor TimerColor => _pomodoro?.Phase switch
    {
        PomodoroPhase.Focus => GlowColor.Red,
        PomodoroPhase.ShortBreak or PomodoroPhase.LongBreak => GlowColor.Green,
        _ => GlowColor.Orange,
    };

    /// <summary>Puts a button for each preset in the settings after "Pomodoro".</summary>
    private void ShowTimerPresets()
    {
        TimerPresets.Children.RemoveRange(1, TimerPresets.Children.Count - 1);

        foreach (TimerPreset preset in _settings.Timers())
        {
            var button = new Button
            {
                Style = TimerPomodoro.Style,
                Margin = TimerPomodoro.Margin,
                Content = preset.Label,
                Tag = preset,
                ToolTip = preset.Name.Length > 0 ? DurationParser.Describe(preset.Duration) : null,
            };
            button.Click += OnTimerPresetClicked;
            TimerPresets.Children.Add(button);
        }
    }

    private void OnTimerPresetClicked(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is TimerPreset preset)
        {
            _pomodoro = null;
            _timerName = preset.Name.Length > 0 ? preset.Name : null;
            StartCountdown(preset.Duration);
        }
    }

    /// <summary>
    /// Swaps the preset buttons for a box to type a length in, or back. The box needs the
    /// keyboard, which the notch otherwise never takes; when done it goes back to the window
    /// that had it.
    /// </summary>
    private void SetTimerEntryOpen(bool open)
    {
        if (_timerEntryOpen == open)
        {
            return;
        }

        _timerEntryOpen = open;
        UpdateTimerRows();
        TimerLabel.Text = open ? TimerEntryHint : TimerTitle;

        if (open)
        {
            NoteForegroundWindow();
            UpdateKeyboardInteraction();
            TimerInput.Clear();
            Activate();
            TimerInput.Focus();
        }
        else
        {
            // Only while the notch still has the keyboard: if the user clicked another window, that one keeps it.
            bool handBack = IsActive && _lastOtherWindow != 0;
            UpdateKeyboardInteraction();
            if (handBack)
            {
                OverlayWindow.SetForeground(_lastOtherWindow);
            }
        }
    }

    /// <summary>
    /// Remembers which window the user is working in. Called regularly rather than only when
    /// the box opens, because by then the notch may already have been given the keyboard.
    /// </summary>
    private void NoteForegroundWindow()
    {
        nint foreground = OverlayWindow.GetForeground();
        if (foreground != 0 && foreground != _hwnd)
        {
            _lastOtherWindow = foreground;
        }
    }

    private void StartTypedTimer()
    {
        if (!DurationParser.TryParse(TimerInput.Text, out TimeSpan duration))
        {
            TimerInput.BorderBrush = ThemeManager.Brush(GlowColor.Red);
            return;
        }

        _pomodoro = null;
        _timerName = null;
        SetTimerEntryOpen(false);
        StartCountdown(duration);
    }

    /// <summary>Under the digits: the presets or the entry box while idle, pause and cancel otherwise.</summary>
    private void UpdateTimerRows()
    {
        bool idle = _countdown.State == CountdownState.Idle;
        TimerPresets.Visibility = TimerCustom.Visibility = idle && !_timerEntryOpen ? Visibility.Visible : Visibility.Collapsed;
        TimerEntry.Visibility = idle && _timerEntryOpen ? Visibility.Visible : Visibility.Collapsed;
        TimerControls.Visibility = idle ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>What is being timed: the Pomodoro phase, the preset's name, or plain "Timer".</summary>
    private string TimerTitle => _pomodoro?.Label ?? _timerName ?? "Timer";

    private void StartCountdown(TimeSpan duration)
    {
        _countdown.Start(duration);
        _timerAnnounced = false;
        _countdownTick.Start();
        UpdateTimer();
    }

    /// <summary>A Pomodoro phase ran out: chime, say what comes next, and carry straight on.</summary>
    private void AdvancePomodoro(PomodoroCycle pomodoro)
    {
        bool focusNext = pomodoro.Advance() == PomodoroPhase.Focus;
        SystemSounds.Asterisk.Play();
        _activities.Publish(new Activity
        {
            Id = PomodoroNoticeId,
            Tier = ActivityTier.Transient,
            Title = focusNext ? "Back to focus" : "Break time",
            Detail = pomodoro.Label,
            Glyph = TimerGlyph,
            Glow = new Glow(focusNext ? GlowColor.Red : GlowColor.Green, GlowPattern.Flash),
            Lifetime = TimeSpan.FromSeconds(4),
        });
        StartCountdown(pomodoro.CurrentDuration);
    }

    private void UpdateTimer()
    {
        CountdownState state = _countdown.State;
        if (state == CountdownState.Finished && _pomodoro is { } pomodoro)
        {
            AdvancePomodoro(pomodoro);
            return;
        }

        TimerLabel.Text = _timerEntryOpen ? TimerEntryHint : TimerTitle;
        string display = state switch
        {
            CountdownState.Idle => "0:00",
            CountdownState.Finished => "Done",
            _ => CountdownTimer.Format(_countdown.Remaining),
        };

        // Ticks arrive four times a second; only touch the UI and the pill when something visible changed.
        string signature = $"{state}:{display}:{TimerTitle}";
        if (signature == _lastTimerDisplay)
        {
            return;
        }

        _lastTimerDisplay = signature;

        TimerText.Text = display;
        if (state == CountdownState.Idle)
        {
            TimerText.ClearValue(TextBlock.ForegroundProperty);
        }
        else
        {
            TimerText.Foreground = ThemeManager.Brush(TimerColor);
        }

        UpdateTimerRows();
        TimerPauseResume.Visibility = state == CountdownState.Finished ? Visibility.Collapsed : Visibility.Visible;
        TimerPauseResume.Content = state == CountdownState.Paused ? "Resume" : "Pause";
        TimerReset.Content = state == CountdownState.Finished ? "Dismiss" : "Cancel";

        switch (state)
        {
            case CountdownState.Idle:
                _countdownTick.Stop();
                _activities.Remove(TimerActivityId);
                break;

            case CountdownState.Finished:
                if (!_timerAnnounced)
                {
                    _timerAnnounced = true;
                    SystemSounds.Asterisk.Play();
                }

                PublishTimer(ActivityTier.Attention, TimerTitle, "Done", new Glow(TimerColor, GlowPattern.Pulse));
                break;

            case CountdownState.Paused:
                PublishTimer(ActivityTier.Ongoing, $"{TimerTitle} paused", display, glow: null);
                break;

            // The glow breathes in the same colour as the digits; focus a touch stronger.
            default:
                var running = new Glow(TimerColor, GlowPattern.Breathe, _pomodoro is { Phase: PomodoroPhase.Focus } ? 0.55 : 0.5);
                PublishTimer(ActivityTier.Ongoing, TimerTitle, display, running);
                break;
        }
    }

    private void PublishTimer(ActivityTier tier, string title, string detail, Glow? glow) => _activities.Publish(new Activity
    {
        Id = TimerActivityId,
        Tier = tier,
        Title = title,
        Detail = detail,
        Glyph = TimerGlyph,
        Glow = glow,
    });

    private async Task RefreshCalendarAsync()
    {
        try
        {
            await _calendar.RefreshAsync([.. _settings.CalendarFeeds]);
        }
        catch (Exception)
        {
            // A failed refresh keeps the previous events; the next one is in a few minutes.
        }
    }

    private void UpdateCalendar()
    {
        DateTimeOffset now = DateTimeOffset.Now;
        CalendarDate.Text = now.ToString("dddd, d MMMM");

        CalendarRow[] rows = [.. _calendar.Upcoming
            .Where(e => e.End > now)
            .Take(MaxCalendarEntries)
            .Select(e => new CalendarRow(DescribeTime(e, now), e.Title, ThemeManager.Brush(FeedColors[e.Feed % FeedColors.Length])))];
        CalendarList.ItemsSource = rows;

        CalendarEmpty.Visibility = rows.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        CalendarEmpty.Text = _settings.CalendarFeeds.Count == 0
            ? "Add a calendar feed in Settings to see upcoming events here."
            : _calendar.FailedFeeds > 0
                ? "Your calendar feed could not be loaded."
                : "Nothing in the next 7 days.";
    }

    private static string DescribeTime(CalendarEntry entry, DateTimeOffset now)
    {
        bool today = entry.Start.Date <= now.Date;
        if (entry.IsAllDay)
        {
            return today ? "Today" : entry.Start.ToString("ddd");
        }

        return today ? entry.Start.ToString("t") : $"{entry.Start:ddd} {entry.Start:t}";
    }

    /// <summary>Stats are only sampled while their tab is on screen.</summary>
    private void UpdateStatsTimer()
    {
        bool run = _expanded && _tab == NotchTab.Stats;
        if (run && !_statsTimer.IsEnabled)
        {
            _statsTimer.Start();
            SampleStats();
        }
        else if (!run)
        {
            _statsTimer.Stop();
        }
    }

    private async void SampleStats()
    {
        if (_statsSampling)
        {
            return;
        }

        _statsSampling = true;
        try
        {
            // Off the UI thread: reading the GPU counters can take tens of milliseconds.
            SystemStats stats = await Task.Run(_stats.Sample);

            StatsCpu.Text = StatsFormat.Percent(stats.CpuUsage);
            StatsCpuChart.Push(stats.CpuUsage);
            StatsMemory.Text = StatsFormat.Percent(stats.MemoryUsage);
            StatsMemoryDetail.Text = $"{StatsFormat.Bytes(stats.MemoryUsedBytes)} of {StatsFormat.Bytes(stats.MemoryTotalBytes)}";
            StatsMemoryChart.Push(stats.MemoryUsage);
            StatsGpu.Text = stats.GpuUsage is { } gpu ? StatsFormat.Percent(gpu) : "n/a";
            StatsGpuChart.Push(stats.GpuUsage ?? 0);
            StatsDownload.Text = StatsFormat.Rate(stats.DownloadBytesPerSecond);
            StatsUpload.Text = StatsFormat.Rate(stats.UploadBytesPerSecond);
            StatsBattery.Text = stats.BatteryPercent is { } battery ? $"{battery}%" : "None";
            if (_batteryPercent != stats.BatteryPercent)
            {
                _batteryPercent = stats.BatteryPercent;
                UpdateBatteryColor();
            }
        }
        catch (Exception)
        {
            // A failed sample is skipped; the next tick tries again.
        }
        finally
        {
            _statsSampling = false;
        }
    }

    private sealed record CalendarRow(string When, string Title, Brush Dot);
}
