using Microsoft.Extensions.Hosting;

namespace SocAlytics.Analysts.Manager.Diagnostics;

/// <summary>Samples host CPU and memory every 5 seconds and keeps only the latest sample in memory; it never leaves the Manager.</summary>
public sealed class HostUtilizationSampler(IHostUtilizationSource source, TimeProvider time) : BackgroundService
{
    public static readonly TimeSpan Period = TimeSpan.FromSeconds(5);

    private readonly Lock gate = new();
    private CpuTimes? previousCpu;
    private HostUtilizationSample? latest;

    public HostUtilizationSample? Latest
    {
        get
        {
            lock (gate)
            {
                return latest;
            }
        }
    }

    /// <summary>Takes one sample; the first one has no CPU value.</summary>
    public HostUtilizationSample SampleOnce()
    {
        HostReading reading;
        try
        {
            reading = source.Read();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            reading = default;
        }

        lock (gate)
        {
            var sample = new HostUtilizationSample(
                time.GetUtcNow(),
                HostUtilizationReaders.CpuPercent(previousCpu, reading.Cpu),
                ToLong(reading.MemoryUsedBytes),
                ToLong(reading.MemoryTotalBytes));
            previousCpu = reading.Cpu;
            latest = sample;
            return sample;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        SampleOnce();
        using var timer = new PeriodicTimer(Period, time);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            SampleOnce();
        }
    }

    private static long? ToLong(ulong? value) => value is { } v && v <= long.MaxValue ? (long)v : null;
}
