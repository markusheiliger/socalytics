namespace SocAlytics.Analysts.Manager.Work;

/// <summary>What the host can give one attempt at most; a job pinning more is never claimed here.</summary>
public sealed record HostCapacity(int CpuMillicores, long MemoryMebibytes)
{
    public static HostCapacity Detect() =>
        new(Environment.ProcessorCount * 1000, GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024 * 1024));

    public bool Fits(JobResources resources) =>
        resources.CpuMillicores <= CpuMillicores && resources.MemoryMebibytes <= MemoryMebibytes;
}

/// <summary>The checks <see cref="AttemptRunner"/> makes on a fetched delivery before it claims.</summary>
public sealed record AttemptAdmission(Func<bool> IsAdmitted, HostCapacity Capacity, Func<TimeSpan> UnfitDelay);
