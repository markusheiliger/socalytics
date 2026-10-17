using System.Globalization;
using System.Runtime.InteropServices;

namespace SocAlytics.Analysts.Manager.Diagnostics;

/// <summary>Cumulative CPU counters of one reading; <paramref name="Bits"/> is the width after which the counters wrap.</summary>
public readonly record struct CpuTimes(ulong Busy, ulong Idle, int Bits = 64);

/// <summary>One raw reading of the host; a value that cannot be read is null.</summary>
public readonly record struct HostReading(CpuTimes? Cpu, ulong? MemoryUsedBytes, ulong? MemoryTotalBytes);

/// <summary>The latest host utilization; CPU is the busy share of all CPUs between two readings (0-100).</summary>
public sealed record HostUtilizationSample(
    DateTimeOffset SampledAt, decimal? CpuPercent, long? MemoryUsedBytes, long? MemoryTotalBytes);

public interface IHostUtilizationSource
{
    HostReading Read();
}

/// <summary>Per-OS readers of CPU and memory with pure parsers and calculations.</summary>
public static class HostUtilizationReaders
{
    public static IHostUtilizationSource ForCurrentOs()
    {
        if (OperatingSystem.IsLinux())
        {
            return new LinuxSource();
        }

        if (OperatingSystem.IsWindows())
        {
            return new WindowsSource();
        }

        if (OperatingSystem.IsMacOS())
        {
            return new MacSource();
        }

        return new UnsupportedSource();
    }

    /// <summary>Parses the aggregate <c>cpu</c> line of <c>/proc/stat</c>.</summary>
    public static CpuTimes? ParseProcStat(string? content)
    {
        if (content is null)
        {
            return null;
        }

        foreach (var line in content.Split('\n'))
        {
            var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 5 || fields[0] != "cpu")
            {
                continue;
            }

            var values = new ulong[Math.Min(fields.Length - 1, 8)];
            for (var i = 0; i < values.Length; i++)
            {
                if (!ulong.TryParse(fields[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out values[i]))
                {
                    return null;
                }
            }

            // user nice system idle iowait irq softirq steal; guest time is already part of user and nice.
            ulong Field(int index) => index < values.Length ? values[index] : 0;
            var busy = unchecked(Field(0) + Field(1) + Field(2) + Field(5) + Field(6) + Field(7));
            var idle = unchecked(Field(3) + Field(4));
            return new CpuTimes(busy, idle);
        }

        return null;
    }

    /// <summary>Parses <c>/proc/meminfo</c>: used is <c>MemTotal - MemAvailable</c>.</summary>
    public static (ulong? Used, ulong? Total) ParseMeminfo(string? content)
    {
        if (content is null)
        {
            return (null, null);
        }

        ulong? total = null;
        ulong? available = null;
        foreach (var line in content.Split('\n'))
        {
            var separator = line.IndexOf(':');
            if (separator < 0)
            {
                continue;
            }

            var name = line[..separator];
            if (name is not ("MemTotal" or "MemAvailable"))
            {
                continue;
            }

            var parts = line[(separator + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0 || !ulong.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var number))
            {
                continue;
            }

            var bytes = parts.Length > 1 && parts[1].Equals("kB", StringComparison.OrdinalIgnoreCase)
                ? number * 1024
                : number;
            if (name == "MemTotal")
            {
                total = bytes;
            }
            else
            {
                available = bytes;
            }
        }

        return total is { } t && available is { } a && a <= t ? (t - a, t) : (null, total);
    }

    /// <summary>Busy share (0-100, one decimal) between two readings; null without a previous reading or without elapsed time.</summary>
    public static decimal? CpuPercent(CpuTimes? previous, CpuTimes? current)
    {
        if (previous is not { } before || current is not { } now || before.Bits != now.Bits)
        {
            return null;
        }

        var busy = Delta(before.Busy, now.Busy, now.Bits);
        var idle = Delta(before.Idle, now.Idle, now.Bits);
        var total = busy + idle;
        if (total == 0)
        {
            return null;
        }

        var percent = (decimal)busy * 100m / total;
        return Math.Round(Math.Clamp(percent, 0m, 100m), 1);
    }

    /// <summary>Counter difference that survives a wrap of a counter <paramref name="bits"/> wide.</summary>
    public static ulong Delta(ulong previous, ulong current, int bits)
    {
        var difference = unchecked(current - previous);
        return bits >= 64 ? difference : difference & ((1UL << bits) - 1);
    }

    /// <summary>Used pages of a <c>vm_statistics64</c> structure: active + wired + compressor pages.</summary>
    public static ulong? ParseVmInfo64UsedPages(ReadOnlySpan<byte> data)
    {
        if (data.Length < 132)
        {
            return null;
        }

        // vm_statistics64: active_count at 4, wire_count at 12, compressor_page_count at 128.
        ulong active = BitConverter.ToUInt32(data[4..8]);
        ulong wired = BitConverter.ToUInt32(data[12..16]);
        ulong compressed = BitConverter.ToUInt32(data[128..132]);
        return active + wired + compressed;
    }

    private sealed class UnsupportedSource : IHostUtilizationSource
    {
        public HostReading Read() => default;
    }

    private sealed class LinuxSource : IHostUtilizationSource
    {
        public HostReading Read()
        {
            var cpu = ParseProcStat(TryRead("/proc/stat"));
            var (used, total) = ParseMeminfo(TryRead("/proc/meminfo"));
            return new HostReading(cpu, used, total);
        }

        private static string? TryRead(string path)
        {
            try
            {
                return File.ReadAllText(path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }

    private sealed class WindowsSource : IHostUtilizationSource
    {
        public HostReading Read()
        {
            CpuTimes? cpu = null;
            ulong? used = null;
            ulong? total = null;
            try
            {
                if (GetSystemTimes(out var idle, out var kernel, out var user))
                {
                    // Kernel time includes idle time.
                    cpu = new CpuTimes(unchecked(kernel + user - idle), idle);
                }

                var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
                if (GlobalMemoryStatusEx(ref status))
                {
                    total = status.TotalPhys;
                    used = status.TotalPhys >= status.AvailPhys ? status.TotalPhys - status.AvailPhys : null;
                }
            }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
            {
            }

            return new HostReading(cpu, used, total);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MemoryStatusEx
        {
            public uint Length;
            public uint MemoryLoad;
            public ulong TotalPhys;
            public ulong AvailPhys;
            public ulong TotalPageFile;
            public ulong AvailPageFile;
            public ulong TotalVirtual;
            public ulong AvailVirtual;
            public ulong AvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx status);
    }

    private sealed class MacSource : IHostUtilizationSource
    {
        private const string Lib = "libSystem";
        private const int HostCpuLoadInfo = 3;
        private const int HostVmInfo64 = 4;
        private const int CpuLoadInfoCount = 4;
        private const int VmInfo64Count = 38;

        public HostReading Read()
        {
            CpuTimes? cpu = null;
            ulong? used = null;
            ulong? total = null;
            try
            {
                var host = mach_host_self();
                var load = new uint[CpuLoadInfoCount];
                var loadCount = (uint)CpuLoadInfoCount;
                if (host_statistics(host, HostCpuLoadInfo, load, ref loadCount) == 0)
                {
                    // user, system, idle, nice ticks are 32 bit counters.
                    var busy = ((ulong)load[0] + load[1] + load[3]) & uint.MaxValue;
                    cpu = new CpuTimes(busy, load[2], 32);
                }

                var vm = new byte[VmInfo64Count * 4];
                var vmCount = (uint)VmInfo64Count;
                if (host_statistics64(host, HostVmInfo64, vm, ref vmCount) == 0
                    && ParseVmInfo64UsedPages(vm) is { } pages)
                {
                    used = pages * (ulong)Environment.SystemPageSize;
                }

                var size = (nuint)sizeof(ulong);
                if (sysctlbyname("hw.memsize", out var memsize, ref size, IntPtr.Zero, 0) == 0)
                {
                    total = memsize;
                }
            }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
            {
            }

            return new HostReading(cpu, used, total);
        }

        [DllImport(Lib)]
        private static extern uint mach_host_self();

        [DllImport(Lib)]
        private static extern int host_statistics(uint host, int flavor, [Out] uint[] info, ref uint count);

        [DllImport(Lib)]
        private static extern int host_statistics64(uint host, int flavor, [Out] byte[] info, ref uint count);

        [DllImport(Lib)]
        private static extern int sysctlbyname(
            [MarshalAs(UnmanagedType.LPStr)] string name, out ulong value, ref nuint size, IntPtr newValue, nuint newSize);
    }
}
