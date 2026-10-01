using System.Management;
using Notch.Core.Activities;
using Notch.Core.Hud;

namespace Notch.Platform.Hud;

/// <summary>Shows a HUD when the built-in panel's brightness changes. External monitors do not raise this event.</summary>
internal sealed class BrightnessWatcher : IDisposable
{
    private readonly ActivityManager _activities;
    private readonly ManagementEventWatcher _watcher;

    /// <exception cref="ManagementException">The machine has no WMI-controllable display (most desktops).</exception>
    public BrightnessWatcher(ActivityManager activities)
    {
        _activities = activities;
        _watcher = new ManagementEventWatcher(@"root\wmi", "SELECT * FROM WmiMonitorBrightnessEvent");
        _watcher.EventArrived += OnBrightnessChanged;
        try
        {
            _watcher.Start();
        }
        catch
        {
            _watcher.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        _watcher.EventArrived -= OnBrightnessChanged;
        try
        {
            _watcher.Stop();
        }
        catch (Exception)
        {
        }

        _watcher.Dispose();
    }

    private void OnBrightnessChanged(object sender, EventArrivedEventArgs e)
    {
        if (e.NewEvent["Brightness"] is byte percent)
        {
            _activities.Publish(HudActivities.Brightness(percent / 100.0));
        }
    }
}
