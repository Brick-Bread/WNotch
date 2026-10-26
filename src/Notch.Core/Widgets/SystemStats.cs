namespace Notch.Core.Widgets;

/// <summary>One sample of system load.</summary>
/// <param name="CpuUsage">0..1 across all cores.</param>
/// <param name="GpuUsage">0..1 for the busiest GPU engine, or null when it cannot be measured.</param>
/// <param name="BatteryPercent">Null on machines without a battery.</param>
/// <param name="CpuTemperature">Degrees Celsius, or null when the machine does not report it.</param>
/// <param name="GpuTemperature">Degrees Celsius of the hottest GPU, or null when no driver reports it.</param>
public sealed record SystemStats(
    double CpuUsage,
    long MemoryUsedBytes,
    long MemoryTotalBytes,
    double? GpuUsage,
    double DownloadBytesPerSecond,
    double UploadBytesPerSecond,
    int? BatteryPercent,
    double? CpuTemperature = null,
    double? GpuTemperature = null)
{
    public double MemoryUsage => MemoryTotalBytes > 0 ? (double)MemoryUsedBytes / MemoryTotalBytes : 0;
}

public static class StatsFormat
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];

    /// <summary>"512 B", "1.5 MB", "12 GB": one decimal below 10, none above.</summary>
    public static string Bytes(double bytes)
    {
        bytes = Math.Max(0, bytes);
        int unit = 0;
        while (bytes >= 1024 && unit < Units.Length - 1)
        {
            bytes /= 1024;
            unit++;
        }

        string number = unit == 0 || bytes >= 10 ? bytes.ToString("0") : bytes.ToString("0.0");
        return $"{number} {Units[unit]}";
    }

    public static string Rate(double bytesPerSecond) => Bytes(bytesPerSecond) + "/s";

    public static string Percent(double fraction) => $"{Math.Round(Math.Clamp(fraction, 0, 1) * 100)}%";

    /// <summary>"59 °C", or "138 °F" where the region does not use the metric system.</summary>
    public static string Temperature(double celsius, bool metric = true) =>
        metric ? $"{Math.Round(celsius)} °C" : $"{Math.Round((celsius * 9 / 5) + 32)} °F";
}
