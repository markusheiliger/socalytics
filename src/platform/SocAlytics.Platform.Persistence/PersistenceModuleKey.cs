using System.Collections.Immutable;

namespace SocAlytics.Platform.Persistence;

/// <summary>An adopted platform module identity. Instances cannot be created outside this type.</summary>
public sealed class PersistenceModuleKey : IEquatable<PersistenceModuleKey>
{
    public static PersistenceModuleKey Club { get; } = new("club", 0);
    public static PersistenceModuleKey IdentityAccess { get; } = new("identity_access", 1);
    public static PersistenceModuleKey Recordings { get; } = new("recordings", 2);
    public static PersistenceModuleKey Registry { get; } = new("registry", 3);
    public static PersistenceModuleKey Analysis { get; } = new("analysis", 4);
    public static PersistenceModuleKey AgentOrchestration { get; } = new("agent_orchestration", 5);

    /// <summary>All adopted modules in the deterministic migration order.</summary>
    public static ImmutableArray<PersistenceModuleKey> All { get; } =
        [Club, IdentityAccess, Recordings, Registry, Analysis, AgentOrchestration];

    private PersistenceModuleKey(string schema, int order)
    {
        Schema = schema;
        Order = order;
    }

    /// <summary>Stable module key, identical to the owned schema name.</summary>
    public string Key => Schema;

    public string Schema { get; }

    public string OwnerRole => $"{Schema}_owner";

    public string RuntimeRole => $"{Schema}_runtime";

    internal int Order { get; }

    public static PersistenceModuleKey Parse(string key) =>
        All.FirstOrDefault(m => string.Equals(m.Key, key, StringComparison.Ordinal))
        ?? throw new ArgumentException($"'{key}' is not an adopted persistence module key.", nameof(key));

    public bool Equals(PersistenceModuleKey? other) => ReferenceEquals(this, other);

    public override bool Equals(object? obj) => Equals(obj as PersistenceModuleKey);

    public override int GetHashCode() => Order;

    public override string ToString() => Key;
}
