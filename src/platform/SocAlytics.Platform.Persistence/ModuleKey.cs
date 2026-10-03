namespace SocAlytics.Platform.Persistence;

/// <summary>
/// One of the adopted module identities. Instances cannot be created outside this type,
/// so the set of keys, their schema names, and their database roles are fixed.
/// </summary>
public sealed class ModuleKey : IEquatable<ModuleKey>
{
    public static ModuleKey Club { get; } = new("club");
    public static ModuleKey IdentityAccess { get; } = new("identity_access");
    public static ModuleKey Recordings { get; } = new("recordings");
    public static ModuleKey Registry { get; } = new("registry");
    public static ModuleKey Analysis { get; } = new("analysis");
    public static ModuleKey AgentOrchestration { get; } = new("agent_orchestration");

    /// <summary>The adopted modules in deterministic migration order.</summary>
    public static IReadOnlyList<ModuleKey> All { get; } =
        [Club, IdentityAccess, Recordings, Registry, Analysis, AgentOrchestration];

    private ModuleKey(string name)
    {
        Name = name;
    }

    /// <summary>The stable module key, which is also the owned schema name.</summary>
    public string Name { get; }

    public string Schema => Name;

    public string OwnerRole => Name + "_owner";

    public string RuntimeRole => Name + "_runtime";

    internal int Order => All.ToList().IndexOf(this);

    public bool Equals(ModuleKey? other) => ReferenceEquals(this, other);

    public override bool Equals(object? obj) => ReferenceEquals(this, obj);

    public override int GetHashCode() => Name.GetHashCode(StringComparison.Ordinal);

    public override string ToString() => Name;
}
