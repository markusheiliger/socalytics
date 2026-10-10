namespace SocAlytics.Analysts.Manager.Runtime;

/// <summary>Built-in adapters keyed by profile. No discovery or loading of external extensions; ships empty.</summary>
public sealed class RuntimeAdapterCatalog
{
    private readonly Dictionary<string, IContainerRuntimeAdapter> adapters;

    public RuntimeAdapterCatalog(IEnumerable<IContainerRuntimeAdapter> builtIn)
    {
        adapters = builtIn.ToDictionary(a => a.Profile, StringComparer.Ordinal);
    }

    public IContainerRuntimeAdapter? Find(string? profile) =>
        profile is not null && adapters.TryGetValue(profile, out var adapter) ? adapter : null;
}
