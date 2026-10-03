namespace SocAlytics.Platform.Persistence;

/// <summary>
/// The closed set of adopted capability modules. Instances cannot be created outside this type,
/// so module keys, schemas, and database roles are fixed.
/// </summary>
public sealed class PersistenceModule : IEquatable<PersistenceModule>
{
    public static PersistenceModule Club { get; } = new("club", 0);
    public static PersistenceModule IdentityAccess { get; } = new("identity_access", 1);
    public static PersistenceModule Recordings { get; } = new("recordings", 2);
    public static PersistenceModule Registry { get; } = new("registry", 3);
    public static PersistenceModule Analysis { get; } = new("analysis", 4);
    public static PersistenceModule AgentOrchestration { get; } = new("agent_orchestration", 5);

    /// <summary>Adopted modules in the deterministic migration order.</summary>
    public static IReadOnlyList<PersistenceModule> All { get; } =
    [
        Club, IdentityAccess, Recordings, Registry, Analysis, AgentOrchestration,
    ];

    private PersistenceModule(string key, int order)
    {
        Key = key;
        Order = order;
    }

    public string Key { get; }

    public string Schema => Key;

    public string OwnerRole => Key + "_owner";

    public string RuntimeRole => Key + "_runtime";

    internal int Order { get; }

    public bool Equals(PersistenceModule? other) => ReferenceEquals(this, other);

    public override bool Equals(object? obj) => Equals(obj as PersistenceModule);

    public override int GetHashCode() => Key.GetHashCode(StringComparison.Ordinal);

    public override string ToString() => Key;
}
