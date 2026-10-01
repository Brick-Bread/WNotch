using Notch.Core.Activities;
using Notch.Core.Hud;
using Windows.System.Power;

namespace Notch.Platform.Hud;

/// <summary>Shows a HUD when the charger is connected or removed, and at low-battery marks.</summary>
internal sealed class PowerWatcher : IDisposable
{
    private readonly ActivityManager _activities;
    private readonly PowerHudTracker _tracker = new();
    private readonly Lock _gate = new();

    public PowerWatcher(ActivityManager activities)
    {
        _activities = activities;
        PowerManager.PowerSupplyStatusChanged += OnPowerChanged;
        PowerManager.BatteryStatusChanged += OnPowerChanged;
        PowerManager.RemainingChargePercentChanged += OnPowerChanged;
        Read();
    }

    public void Dispose()
    {
        PowerManager.PowerSupplyStatusChanged -= OnPowerChanged;
        PowerManager.BatteryStatusChanged -= OnPowerChanged;
        PowerManager.RemainingChargePercentChanged -= OnPowerChanged;
    }

    private void OnPowerChanged(object? sender, object e) => Read();

    private void Read()
    {
        Activity? hud;
        lock (_gate)
        {
            hud = _tracker.Update(
                hasBattery: PowerManager.BatteryStatus != BatteryStatus.NotPresent,
                pluggedIn: PowerManager.PowerSupplyStatus != PowerSupplyStatus.NotPresent,
                percent: PowerManager.RemainingChargePercent);
        }

        if (hud is not null)
        {
            _activities.Publish(hud);
        }
    }
}
