using System.Collections.ObjectModel;

namespace SocAlytics.Platform.Persistence;

/// <summary>
/// Fixed identity of an adopted platform module. The key doubles as the module's PostgreSQL schema name.
/// Instances cannot be created outside this type.
/// </summary>
public sealed class PersistenceModuleKey : IEquatable<PersistenceModuleKey>
{
    public static PersistenceModuleKey Club { get; } = new("club");
    public static PersistenceModuleKey IdentityAccess { get; } = new("identity_access");
    public static PersistenceModuleKey Recordings { get; } = new("recordings");
    public static PersistenceModuleKey Registry { get; } = new("registry");
    public static PersistenceModuleKey Analysis { get; } = new("analysis");
    public static PersistenceModuleKey AgentOrchestration { get; } = new("agent_orchestration");

    /// <summary>All adopted modules in deterministic migration order.</summary>
    public static IReadOnlyList<PersistenceModuleKey> All { get; } = new ReadOnlyCollection<PersistenceModuleKey>(
        [Club, IdentityAccess, Recordings, Registry, Analysis, AgentOrchestration]);

    private PersistenceModuleKey(string name) => Name = name;

    public string Name { get; }

    public string SchemaName => Name;

    public string OwnerRoleName => $"{Name}_owner";

    public string RuntimeRoleName => $"{Name}_runtime";

    internal int Order => All.ToList().IndexOf(this);

    public bool Equals(PersistenceModuleKey? other) => ReferenceEquals(this, other);

    public override bool Equals(object? obj) => obj is PersistenceModuleKey other && Equals(other);

    public override int GetHashCode() => Name.GetHashCode(StringComparison.Ordinal);

    public override string ToString() => Name;
}
