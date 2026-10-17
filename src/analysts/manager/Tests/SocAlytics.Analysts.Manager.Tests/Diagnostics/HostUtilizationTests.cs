using Microsoft.Extensions.Time.Testing;
using Shouldly;
using SocAlytics.Analysts.Manager.Diagnostics;
using Xunit;

namespace SocAlytics.Analysts.Manager.Tests.HostUtilizationSampling;

public sealed class HostUtilizationTests
{
    public static bool IsLinux => OperatingSystem.IsLinux();
    public static bool IsWindows => OperatingSystem.IsWindows();
    public static bool IsMacOS => OperatingSystem.IsMacOS();

    private const string ProcStat = "cpu  100 10 50 800 20 5 5 10 0 0\ncpu0 50 5 25 400 10 2 3 5 0 0\nintr 1\n";

    private const string Meminfo = "MemTotal:       16000000 kB\nMemFree:         1000000 kB\nMemAvailable:    6000000 kB\n";

    [Fact]
    public void ParsesAggregateCpuLine()
    {
        var times = HostUtilizationReaders.ParseProcStat(ProcStat);

        times.ShouldBe(new CpuTimes(100 + 10 + 50 + 5 + 5 + 10, 800 + 20));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("cpu0 1 2 3 4\n")]
    [InlineData("cpu  1 2 x 4 5\n")]
    [InlineData("cpu  1 2 3\n")]
    public void UnreadableProcStatIsNull(string? content) =>
        HostUtilizationReaders.ParseProcStat(content).ShouldBeNull();

    [Fact]
    public void ParsesMeminfoAsTotalMinusAvailable()
    {
        var (used, total) = HostUtilizationReaders.ParseMeminfo(Meminfo);

        total.ShouldBe(16000000UL * 1024);
        used.ShouldBe(10000000UL * 1024);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("MemTotal: 100 kB\n")]
    [InlineData("MemAvailable: 100 kB\n")]
    [InlineData("MemTotal: 100 kB\nMemAvailable: 200 kB\n")]
    public void IncompleteMeminfoHasNoUsedValue(string? content) =>
        HostUtilizationReaders.ParseMeminfo(content).Used.ShouldBeNull();

    [Fact]
    public void CpuPercentIsBusyShareBetweenReadings()
    {
        var percent = HostUtilizationReaders.CpuPercent(new CpuTimes(100, 900), new CpuTimes(400, 1600));

        percent.ShouldBe(30.0m);
    }

    [Fact]
    public void CpuPercentHandlesCounterWrap()
    {
        var percent = HostUtilizationReaders.CpuPercent(
            new CpuTimes(uint.MaxValue - 9, 100, 32), new CpuTimes(10, 120, 32));

        percent.ShouldBe(50.0m);
        HostUtilizationReaders.Delta(ulong.MaxValue - 1, 3, 64).ShouldBe(5UL);
    }

    [Fact]
    public void CpuPercentIsNullWithoutTwoUsableReadings()
    {
        HostUtilizationReaders.CpuPercent(null, new CpuTimes(1, 1)).ShouldBeNull();
        HostUtilizationReaders.CpuPercent(new CpuTimes(1, 1), null).ShouldBeNull();
        HostUtilizationReaders.CpuPercent(new CpuTimes(5, 5), new CpuTimes(5, 5)).ShouldBeNull();
        HostUtilizationReaders.CpuPercent(new CpuTimes(1, 1, 32), new CpuTimes(2, 2)).ShouldBeNull();
    }

    [Fact]
    public void ParsesMacVmInfoPages()
    {
        var data = new byte[152];
        BitConverter.GetBytes(10u).CopyTo(data, 4);
        BitConverter.GetBytes(20u).CopyTo(data, 12);
        BitConverter.GetBytes(30u).CopyTo(data, 128);

        HostUtilizationReaders.ParseVmInfo64UsedPages(data).ShouldBe(60UL);
        HostUtilizationReaders.ParseVmInfo64UsedPages(new byte[16]).ShouldBeNull();
    }

    [Fact]
    public void FirstSampleHasNoCpuAndLaterOnesDoAndOnlyTheLatestIsKept()
    {
        var time = new FakeTimeProvider();
        var source = new ScriptedSource(
            new HostReading(new CpuTimes(100, 900), 1000, 4000),
            new HostReading(new CpuTimes(200, 1800), 2000, 4000));
        var sampler = new HostUtilizationSampler(source, time);

        sampler.Latest.ShouldBeNull();
        var first = sampler.SampleOnce();
        first.CpuPercent.ShouldBeNull();
        first.MemoryUsedBytes.ShouldBe(1000);

        time.Advance(HostUtilizationSampler.Period);
        var second = sampler.SampleOnce();

        second.CpuPercent.ShouldBe(10.0m);
        second.MemoryTotalBytes.ShouldBe(4000);
        second.SampledAt.ShouldBe(first.SampledAt + HostUtilizationSampler.Period);
        sampler.Latest.ShouldBe(second);
    }

    [Fact]
    public void UnreadableValuesAreNullAndFailuresAreAbsorbed()
    {
        var sampler = new HostUtilizationSampler(new ScriptedSource(new HostReading()), new FakeTimeProvider());

        var sample = sampler.SampleOnce();

        sample.CpuPercent.ShouldBeNull();
        sample.MemoryUsedBytes.ShouldBeNull();
        sample.MemoryTotalBytes.ShouldBeNull();

        var failing = new HostUtilizationSampler(new ThrowingSource(), new FakeTimeProvider());
        failing.SampleOnce().MemoryTotalBytes.ShouldBeNull();
    }

    [Fact(Skip = "Linux only", SkipUnless = nameof(IsLinux), SkipType = typeof(HostUtilizationTests))]
    public void LiveLinuxReaderReturnsPlausibleValues()
    {
        var source = HostUtilizationReaders.ForCurrentOs();
        var first = source.Read();
        Thread.SpinWait(1_000_000);
        var second = source.Read();

        first.Cpu.ShouldNotBeNull();
        var total = second.MemoryTotalBytes.ShouldNotBeNull();
        total.ShouldBeGreaterThan(0UL);
        second.MemoryUsedBytes.ShouldNotBeNull().ShouldBeLessThanOrEqualTo(total);
        var percent = HostUtilizationReaders.CpuPercent(first.Cpu, second.Cpu);
        if (percent is { } value)
        {
            value.ShouldBeInRange(0m, 100m);
        }
    }

    [Fact(Skip = "Windows only", SkipUnless = nameof(IsWindows), SkipType = typeof(HostUtilizationTests))]
    public void LiveWindowsReaderReturnsPlausibleValues() => AssertLivePlausible();

    [Fact(Skip = "macOS only", SkipUnless = nameof(IsMacOS), SkipType = typeof(HostUtilizationTests))]
    public void LiveMacReaderReturnsPlausibleValues() => AssertLivePlausible();

    private static void AssertLivePlausible()
    {
        var reading = HostUtilizationReaders.ForCurrentOs().Read();

        reading.Cpu.ShouldNotBeNull();
        var total = reading.MemoryTotalBytes.ShouldNotBeNull();
        total.ShouldBeGreaterThan(0UL);
        reading.MemoryUsedBytes.ShouldNotBeNull().ShouldBeLessThanOrEqualTo(total);
    }

    private sealed class ScriptedSource(params HostReading[] readings) : IHostUtilizationSource
    {
        private int index;

        public HostReading Read() => readings[Math.Min(index++, readings.Length - 1)];
    }

    private sealed class ThrowingSource : IHostUtilizationSource
    {
        public HostReading Read() => throw new InvalidOperationException("unreadable");
    }
}
