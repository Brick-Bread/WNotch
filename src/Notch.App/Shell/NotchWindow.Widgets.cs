using System.Media;
using System.Net.Http;
using System.Windows;
using System.Windows.Threading;
using Notch.Core.Activities;
using Notch.Core.Widgets;
using Notch.Platform.Stats;

namespace Notch.App.Shell;

// The timer and calendar cards on Home, and the Stats tab.
public partial class NotchWindow
{
    private const string TimerActivityId = "timer";
    private const string TimerGlyph = "";
    private const int MaxCalendarEntries = 4;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    private readonly CountdownTimer _countdown = new();
    private readonly DispatcherTimer _countdownTick = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly CalendarService _calendar = new(Http);
    private readonly DispatcherTimer _calendarTimer = new() { Interval = TimeSpan.FromMinutes(15) };
    private readonly SystemStatsSampler _stats = new();
    private readonly DispatcherTimer _statsTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    private string? _lastTimerDisplay;
    private bool _timerAnnounced;
    private bool _statsSampling;

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
            _countdown.Reset();
            UpdateTimer();
        };

        _calendar.Changed += (_, _) => Dispatcher.BeginInvoke(UpdateCalendar);
        _calendarTimer.Tick += (_, _) => RefreshCalendar();
        _calendarTimer.Start();
        UpdateCalendar();
        RefreshCalendar();

        _statsTimer.Tick += (_, _) => SampleStats();
    }

    private void OnTimerPresetClicked(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is string minutes)
        {
            _countdown.Start(TimeSpan.FromMinutes(int.Parse(minutes)));
            _timerAnnounced = false;
            _countdownTick.Start();
            UpdateTimer();
        }
    }

    private void UpdateTimer()
    {
        CountdownState state = _countdown.State;
        string display = state switch
        {
            CountdownState.Idle => "0:00",
            CountdownState.Finished => "Done",
            _ => CountdownTimer.Format(_countdown.Remaining),
        };

        // Ticks arrive four times a second; only touch the UI and the pill when something visible changed.
        string signature = $"{state}:{display}";
        if (signature == _lastTimerDisplay)
        {
            return;
        }

        _lastTimerDisplay = signature;

        TimerText.Text = display;
        TimerPresets.Visibility = state == CountdownState.Idle ? Visibility.Visible : Visibility.Collapsed;
        TimerControls.Visibility = state == CountdownState.Idle ? Visibility.Collapsed : Visibility.Visible;
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

                PublishTimer(ActivityTier.Attention, "Timer", "Done");
                break;

            default:
                PublishTimer(ActivityTier.Ongoing, state == CountdownState.Paused ? "Timer paused" : "Timer", display);
                break;
        }
    }

    private void PublishTimer(ActivityTier tier, string title, string detail) => _activities.Publish(new Activity
    {
        Id = TimerActivityId,
        Tier = tier,
        Title = title,
        Detail = detail,
        Glyph = TimerGlyph,
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
            .Select(e => new CalendarRow(DescribeTime(e, now), e.Title))];
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

    private sealed record CalendarRow(string When, string Title);
}
