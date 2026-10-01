using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using Notch.Core.Widgets;
using Windows.System.Power;

namespace Notch.Platform.Stats;

/// <summary>
/// Samples CPU, memory, GPU, network and battery. Rates are measured between consecutive
/// calls to <see cref="Sample"/>, so the first sample reports zero CPU and network activity.
/// Not thread-safe: call from one thread at a time.
/// </summary>
public sealed partial class SystemStatsSampler
{
    private const string GpuCategory = "GPU Engine";
    private const string GpuCounter = "Utilization Percentage";

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private ulong _lastIdle;
    private ulong _lastTotal;
    private long _lastReceived;
    private long _lastSent;
    private TimeSpan _lastNetworkTime;
    private bool _hasNetworkBaseline;
    private Dictionary<string, CounterSample>? _lastGpuSamples;
    private bool _gpuUnavailable;

    public SystemStats Sample()
    {
        (long used, long total) = ReadMemory();
        (double down, double up) = ReadNetwork();

        return new SystemStats(
            ReadCpu(),
            used,
            total,
            ReadGpu(),
            down,
            up,
            PowerManager.BatteryStatus == BatteryStatus.NotPresent ? null : PowerManager.RemainingChargePercent);
    }

    private double ReadCpu()
    {
        if (!GetSystemTimes(out ulong idle, out ulong kernel, out ulong user))
        {
            return 0;
        }

        // Kernel time includes idle time.
        ulong total = kernel + user;
        ulong idleDelta = idle - _lastIdle;
        ulong totalDelta = total - _lastTotal;
        bool first = _lastTotal == 0;
        _lastIdle = idle;
        _lastTotal = total;

        return first || totalDelta == 0 ? 0 : Math.Clamp(1 - ((double)idleDelta / totalDelta), 0, 1);
    }

    private static (long Used, long Total) ReadMemory()
    {
        var status = new MEMORYSTATUSEX { Length = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        return GlobalMemoryStatusEx(ref status)
            ? ((long)(status.TotalPhysical - status.AvailablePhysical), (long)status.TotalPhysical)
            : (0, 0);
    }

    private (double Down, double Up) ReadNetwork()
    {
        long received = 0, sent = 0;
        try
        {
            foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up
                    || nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                {
                    continue;
                }

                IPInterfaceStatistics statistics = nic.GetIPStatistics();
                received += statistics.BytesReceived;
                sent += statistics.BytesSent;
            }
        }
        catch (NetworkInformationException)
        {
            return (0, 0);
        }

        TimeSpan now = _clock.Elapsed;
        double seconds = (now - _lastNetworkTime).TotalSeconds;
        double down = 0, up = 0;

        // An adapter disappearing makes the totals drop; report zero rather than a negative rate.
        if (_hasNetworkBaseline && seconds > 0)
        {
            down = Math.Max(0, received - _lastReceived) / seconds;
            up = Math.Max(0, sent - _lastSent) / seconds;
        }

        _lastReceived = received;
        _lastSent = sent;
        _lastNetworkTime = now;
        _hasNetworkBaseline = true;
        return (down, up);
    }

    /// <summary>The busiest engine type (3D, video decode, ...) summed over all processes, as Task Manager shows it.</summary>
    private double? ReadGpu()
    {
        if (_gpuUnavailable)
        {
            return null;
        }

        try
        {
            var category = new PerformanceCounterCategory(GpuCategory);
            InstanceDataCollection? instances = category.ReadCategory()[GpuCounter];
            if (instances is null)
            {
                _gpuUnavailable = true;
                return null;
            }

            var current = new Dictionary<string, CounterSample>(instances.Count);
            var usageByEngine = new Dictionary<string, double>();
            foreach (InstanceData instance in instances.Values)
            {
                current[instance.InstanceName] = instance.Sample;
                if (_lastGpuSamples is null || !_lastGpuSamples.TryGetValue(instance.InstanceName, out CounterSample previous))
                {
                    continue;
                }

                // Instance names look like "pid_1234_luid_0x..._phys_0_eng_0_engtype_3D".
                int marker = instance.InstanceName.LastIndexOf("engtype_", StringComparison.Ordinal);
                string engine = marker >= 0 ? instance.InstanceName[marker..] : "";
                double usage = CounterSample.Calculate(previous, instance.Sample);
                usageByEngine[engine] = usageByEngine.GetValueOrDefault(engine) + usage;
            }

            bool first = _lastGpuSamples is null;
            _lastGpuSamples = current;
            return first || usageByEngine.Count == 0 ? 0 : Math.Clamp(usageByEngine.Values.Max() / 100, 0, 1);
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            // No GPU counters on this machine (very old drivers, or counters disabled).
            _gpuUnavailable = true;
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemTimes(out ulong idleTime, out ulong kernelTime, out ulong userTime);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX status);
}
