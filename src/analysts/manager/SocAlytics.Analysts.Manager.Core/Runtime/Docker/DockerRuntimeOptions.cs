namespace SocAlytics.Analysts.Manager.Runtime.Docker;

/// <summary>Bound from <c>Runtime:Docker</c>; the <c>docker-desktop</c> profile's endpoint, resource limits, capability narrowing, and image cache.</summary>
public sealed class DockerRuntimeOptions
{
    public const string SectionName = "Runtime:Docker";
    public const string DefaultEndpoint = "npipe://./pipe/docker_engine";
    public const long DefaultImageCacheBudgetBytes = 20L * 1024 * 1024 * 1024;
    public static readonly TimeSpan DefaultImageCacheMaxIdle = TimeSpan.FromDays(14);

    public string Endpoint { get; set; } = DefaultEndpoint;

    /// <summary>Upper bound of CPUs for all attempts together; null means the host CPU count.</summary>
    public decimal? Cpus { get; set; }

    /// <summary>Upper bound of memory for all attempts together; null means 75% of the runtime's <c>MemTotal</c>.</summary>
    public long? MemoryBytes { get; set; }

    /// <summary>Entries of the form <c>id@version</c>; empty means no narrowing.</summary>
    public List<string> CapabilityAllowlist { get; set; } = [];

    public long ImageCacheBudgetBytes { get; set; } = DefaultImageCacheBudgetBytes;

    public TimeSpan ImageCacheMaxIdle { get; set; } = DefaultImageCacheMaxIdle;

    public decimal EffectiveCpus => Cpus ?? Environment.ProcessorCount;

    public long EffectiveMemoryBytes(long runtimeMemTotal) => MemoryBytes ?? (long)(runtimeMemTotal * 0.75m);

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Endpoint))
        {
            throw new InvalidOperationException($"{SectionName}:Endpoint must not be empty.");
        }

        if (Cpus is <= 0)
        {
            throw new InvalidOperationException($"{SectionName}:Cpus must be greater than 0.");
        }

        if (MemoryBytes is <= 0)
        {
            throw new InvalidOperationException($"{SectionName}:MemoryBytes must be greater than 0.");
        }

        if (ImageCacheBudgetBytes <= 0)
        {
            throw new InvalidOperationException($"{SectionName}:ImageCacheBudgetBytes must be greater than 0.");
        }

        if (ImageCacheMaxIdle <= TimeSpan.Zero)
        {
            throw new InvalidOperationException($"{SectionName}:ImageCacheMaxIdle must be greater than 0.");
        }

        foreach (var entry in CapabilityAllowlist)
        {
            var at = entry?.IndexOf('@') ?? -1;
            if (at <= 0 || at == entry!.Length - 1)
            {
                throw new InvalidOperationException($"{SectionName}:CapabilityAllowlist entries must have the form id@version.");
            }
        }
    }
}
