namespace SocAlytics.Platform.Persistence;

public enum ModuleIdentity
{
    Club,
    IdentityAccess,
    Recordings,
    Registry,
    Analysis,
    AgentOrchestration
}

internal static class ModuleIdentityCatalog
{
    internal static IReadOnlyList<ModuleIdentity> Ordered { get; } =
    [
        ModuleIdentity.Club,
        ModuleIdentity.IdentityAccess,
        ModuleIdentity.Recordings,
        ModuleIdentity.Registry,
        ModuleIdentity.Analysis,
        ModuleIdentity.AgentOrchestration
    ];

    internal static string GetKey(ModuleIdentity module) => module switch
    {
        ModuleIdentity.Club => "club",
        ModuleIdentity.IdentityAccess => "identity_access",
        ModuleIdentity.Recordings => "recordings",
        ModuleIdentity.Registry => "registry",
        ModuleIdentity.Analysis => "analysis",
        ModuleIdentity.AgentOrchestration => "agent_orchestration",
        _ => throw new ArgumentOutOfRangeException(nameof(module), module, "Unknown module identity.")
    };
}
