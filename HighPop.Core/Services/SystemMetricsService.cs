using System.Diagnostics;
using System.IO;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace HighPop.Services;

public record SystemMetrics(double CpuPercent, double MemUsedGb, double MemTotalGb);

public record DriveStats(string Name, double UsedGb, double TotalGb)
{
    public double UsedPercent => TotalGb > 0 ? UsedGb / TotalGb * 100.0 : 0;
}

public class SystemMetricsService : IDisposable
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    private PerformanceCounter? _cpuCounter;
    private readonly System.Timers.Timer _timer;
    private int _refreshing;
    private DateTime _nextDriveRefreshUtc = DateTime.MinValue;
    private static readonly TimeSpan DriveRefreshInterval = TimeSpan.FromSeconds(30);

    public float CpuPercent { get; private set; }
    public long RamUsedMb { get; private set; }
    public long RamTotalMb { get; private set; }
    public IReadOnlyList<DriveStats> Drives { get; private set; } = [];

    public event Action? MetricsUpdated;

    public SystemMetrics Current => new(CpuPercent, RamUsedMb / 1024.0, RamTotalMb / 1024.0);

    public SystemMetricsService()
    {
        if (OperatingSystem.IsWindows())
            _cpuCounter = TryCreateCpuCounter();
        else
            SampleLinuxCpu(); // the first sample only primes the delta

        _timer = new System.Timers.Timer(2000);
        _timer.Elapsed += (_, _) => Refresh();
        _timer.AutoReset = true;
        _timer.Start();

        Refresh();
    }

    private void Refresh()
    {
        if (Interlocked.Exchange(ref _refreshing, 1) != 0) return;
        try
        {
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    if (_cpuCounter != null)
                        CpuPercent = Math.Clamp(_cpuCounter.NextValue(), 0f, 100f);
                }
                else
                {
                    CpuPercent = SampleLinuxCpu();
                }
            }
            catch { }

            try
            {
                if (OperatingSystem.IsWindows()) ReadWindowsMemory();
                else ReadLinuxMemory();
            }
            catch { }

            var now = DateTime.UtcNow;
            if (now >= _nextDriveRefreshUtc)
            {
                _nextDriveRefreshUtc = now + DriveRefreshInterval;
                try
                {
                    Drives = DriveInfo.GetDrives()
                        .Where(IsReportableDrive)
                        .Select(d => new DriveStats(
                            d.Name,
                            Math.Round((d.TotalSize - d.AvailableFreeSpace) / 1_073_741_824.0, 1),
                            Math.Round(d.TotalSize / 1_073_741_824.0, 1)))
                        .ToList();
                }
                catch { }
            }

            // Runs on a timer thread: a failing subscriber must not end the process.
            try { MetricsUpdated?.Invoke(); }
            catch (Exception ex) { System.Diagnostics.Trace.TraceError($"Metrics subscriber failed: {ex.Message}"); }
        }
        finally
        {
            Volatile.Write(ref _refreshing, 0);
        }
    }

    [SupportedOSPlatform("windows")]
    private static PerformanceCounter? TryCreateCpuCounter()
    {
        try
        {
            var counter = new PerformanceCounter("Processor", "% Processor Time", "_Total", readOnly: true);
            counter.NextValue(); // first call always returns 0 — prime it
            return counter;
        }
        catch { return null; }
    }

    private void ReadWindowsMemory()
    {
        var mem = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref mem)) return;
        RamTotalMb = (long)(mem.ullTotalPhys / 1024 / 1024);
        var availMb = (long)(mem.ullAvailPhys / 1024 / 1024);
        RamUsedMb  = RamTotalMb - availMb;
    }

    // ── Linux (/proc) ────────────────────────────────────────────────────────

    private long _prevCpuIdle, _prevCpuTotal;

    private float SampleLinuxCpu()
    {
        var line = File.ReadLines("/proc/stat").FirstOrDefault();
        if (line == null || !TryParseProcStatCpu(line, out var idle, out var total)) return CpuPercent;
        var percent = ComputeCpuPercent(_prevCpuIdle, _prevCpuTotal, idle, total);
        _prevCpuIdle = idle;
        _prevCpuTotal = total;
        return percent;
    }

    private void ReadLinuxMemory()
    {
        if (!TryParseMemInfo(File.ReadAllText("/proc/meminfo"), out var totalMb, out var usedMb)) return;
        RamTotalMb = totalMb;
        RamUsedMb = usedMb;
    }

    /// <summary>Parses the aggregate "cpu" line of /proc/stat into idle (idle + iowait) and total jiffies.</summary>
    internal static bool TryParseProcStatCpu(string line, out long idle, out long total)
    {
        idle = total = 0;
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 5 || parts[0] != "cpu") return false;
        // user nice system idle iowait irq softirq steal (guest time is already inside user/nice)
        for (var i = 1; i < parts.Length && i <= 8; i++)
        {
            if (!long.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out var value)) return false;
            total += value;
            if (i is 4 or 5) idle += value;
        }
        return true;
    }

    internal static float ComputeCpuPercent(long previousIdle, long previousTotal, long idle, long total)
    {
        var totalDelta = total - previousTotal;
        if (totalDelta <= 0) return 0f;
        var busy = 1.0 - (double)(idle - previousIdle) / totalDelta;
        return (float)Math.Clamp(busy * 100.0, 0.0, 100.0);
    }

    /// <summary>Total and used memory in MB from /proc/meminfo; "used" is total minus MemAvailable.</summary>
    internal static bool TryParseMemInfo(string text, out long totalMb, out long usedMb)
    {
        totalMb = usedMb = 0;
        long totalKb = -1, availableKb = -1, freeKb = 0, buffersKb = 0, cachedKb = 0;
        foreach (var line in text.Split('\n'))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var parts = line[(colon + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0 || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var kb)) continue;
            switch (line[..colon])
            {
                case "MemTotal": totalKb = kb; break;
                case "MemAvailable": availableKb = kb; break;
                case "MemFree": freeKb = kb; break;
                case "Buffers": buffersKb = kb; break;
                case "Cached": cachedKb = kb; break;
            }
        }
        if (totalKb <= 0) return false;
        if (availableKb < 0) availableKb = freeKb + buffersKb + cachedKb; // kernels before 3.14
        totalMb = totalKb / 1024;
        usedMb = Math.Max(0, (totalKb - availableKb) / 1024);
        return true;
    }

    private static readonly HashSet<string> VirtualFileSystems = new(StringComparer.OrdinalIgnoreCase)
    {
        "tmpfs", "devtmpfs", "squashfs", "proc", "sysfs", "cgroup", "cgroup2", "devpts",
        "efivarfs", "ramfs", "securityfs", "debugfs", "tracefs", "overlay", "fuse.snapfuse",
    };

    /// <summary>Fixed, ready drives; on Linux also skips virtual mounts (tmpfs, snap, containers).</summary>
    internal static bool IsReportableDrive(DriveInfo drive)
    {
        if (drive.DriveType != DriveType.Fixed || !drive.IsReady) return false;
        if (OperatingSystem.IsWindows()) return true;
        return drive.TotalSize > 0
               && !VirtualFileSystems.Contains(drive.DriveFormat)
               && !drive.Name.StartsWith("/snap", StringComparison.Ordinal);
    }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Dispose();
        if (OperatingSystem.IsWindows()) _cpuCounter?.Dispose();
    }
}
