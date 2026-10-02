namespace SocAlytics.Platform.Persistence;

public sealed record PersistenceModuleIdentity
{
    private PersistenceModuleIdentity(string key)
    {
        Key = key;
    }

    public string Key { get; }

    public static PersistenceModuleIdentity Club { get; } = new("club");

    public static PersistenceModuleIdentity IdentityAccess { get; } = new("identity_access");

    public static PersistenceModuleIdentity Recordings { get; } = new("recordings");

    public static PersistenceModuleIdentity Registry { get; } = new("registry");

    public static PersistenceModuleIdentity Analysis { get; } = new("analysis");

    public static PersistenceModuleIdentity AgentOrchestration { get; } = new("agent_orchestration");

    public static IReadOnlyList<PersistenceModuleIdentity> All { get; } =
        Array.AsReadOnly(
        [
            Club,
            IdentityAccess,
            Recordings,
            Registry,
            Analysis,
            AgentOrchestration
        ]);
}
