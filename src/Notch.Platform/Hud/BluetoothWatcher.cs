using System.Collections.Concurrent;
using Notch.Core.Activities;
using Notch.Core.Hud;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;

namespace Notch.Platform.Hud;

/// <summary>Shows a HUD when a paired Bluetooth device connects or disconnects.</summary>
internal sealed class BluetoothWatcher : IDisposable
{
    private const string IsConnectedProperty = "System.Devices.Aep.IsConnected";

    private readonly ActivityManager _activities;
    private readonly ConcurrentDictionary<string, Device> _devices = new();
    private readonly DeviceWatcher[] _watchers;

    public BluetoothWatcher(ActivityManager activities)
    {
        _activities = activities;

        // Classic (headsets, speakers) and Low Energy (mice, keyboards) are enumerated separately.
        _watchers =
        [
            CreateWatcher(BluetoothDevice.GetDeviceSelectorFromPairingState(true)),
            CreateWatcher(BluetoothLEDevice.GetDeviceSelectorFromPairingState(true)),
        ];

        foreach (DeviceWatcher watcher in _watchers)
        {
            watcher.Start();
        }
    }

    public void Dispose()
    {
        foreach (DeviceWatcher watcher in _watchers)
        {
            watcher.Added -= OnAdded;
            watcher.Updated -= OnUpdated;
            watcher.Removed -= OnRemoved;
            try
            {
                watcher.Stop();
            }
            catch (Exception)
            {
                // Stop throws if the watcher never finished starting.
            }
        }
    }

    private DeviceWatcher CreateWatcher(string selector)
    {
        DeviceWatcher watcher = DeviceInformation.CreateWatcher(selector, [IsConnectedProperty], DeviceInformationKind.AssociationEndpoint);
        watcher.Added += OnAdded;
        watcher.Updated += OnUpdated;
        watcher.Removed += OnRemoved;
        return watcher;
    }

    // Added carries each device's state at startup, so it only records the baseline.
    private void OnAdded(DeviceWatcher sender, DeviceInformation info) =>
        _devices[info.Id] = new Device(info.Name, ReadIsConnected(info.Properties) ?? false);

    private void OnUpdated(DeviceWatcher sender, DeviceInformationUpdate update)
    {
        if (ReadIsConnected(update.Properties) is not { } connected
            || !_devices.TryGetValue(update.Id, out Device? device)
            || device.IsConnected == connected)
        {
            return;
        }

        _devices[update.Id] = device with { IsConnected = connected };
        if (!string.IsNullOrWhiteSpace(device.Name))
        {
            _activities.Publish(HudActivities.Bluetooth(device.Name, connected));
        }
    }

    private void OnRemoved(DeviceWatcher sender, DeviceInformationUpdate update) =>
        _devices.TryRemove(update.Id, out _);

    private static bool? ReadIsConnected(IReadOnlyDictionary<string, object> properties) =>
        properties.TryGetValue(IsConnectedProperty, out object? value) && value is bool connected ? connected : null;

    private sealed record Device(string Name, bool IsConnected);
}
