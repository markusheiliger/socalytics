using System.Collections.Immutable;

namespace SocAlytics.Platform.Persistence;

/// <summary>A fixed adopted module identity. Modules cannot define their own schema names.</summary>
public sealed class PersistenceModuleKey : IEquatable<PersistenceModuleKey>
{
    public static PersistenceModuleKey Club { get; } = new("club", 0);
    public static PersistenceModuleKey IdentityAccess { get; } = new("identity_access", 1);
    public static PersistenceModuleKey Recordings { get; } = new("recordings", 2);
    public static PersistenceModuleKey Registry { get; } = new("registry", 3);
    public static PersistenceModuleKey Analysis { get; } = new("analysis", 4);
    public static PersistenceModuleKey AgentOrchestration { get; } = new("agent_orchestration", 5);

    /// <summary>All adopted modules in migration order.</summary>
    public static ImmutableArray<PersistenceModuleKey> All { get; } =
        [Club, IdentityAccess, Recordings, Registry, Analysis, AgentOrchestration];

    private PersistenceModuleKey(string schema, int order)
    {
        Schema = schema;
        Order = order;
    }

    /// <summary>The stable module key, equal to the owned PostgreSQL schema name.</summary>
    public string Schema { get; }

    public string OwnerRole => $"{Schema}_owner";

    public string RuntimeRole => $"{Schema}_runtime";

    internal int Order { get; }

    public static PersistenceModuleKey FromSchema(string schema)
    {
        ArgumentException.ThrowIfNullOrEmpty(schema);

        return All.FirstOrDefault(key => key.Schema == schema)
            ?? throw new ArgumentException($"'{schema}' is not an adopted persistence module.", nameof(schema));
    }

    public bool Equals(PersistenceModuleKey? other) => ReferenceEquals(this, other);

    public override bool Equals(object? obj) => ReferenceEquals(this, obj);

    public override int GetHashCode() => Order;

    public override string ToString() => Schema;
}
