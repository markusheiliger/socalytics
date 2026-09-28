namespace SocAlytics.Platform.Persistence;

/// <summary>
/// The fixed, closed set of platform module identities. Each identity owns exactly one
/// adopted PostgreSQL schema; no other module key can be introduced or requested at runtime.
/// </summary>
public enum ModuleKey
{
    Club,
    IdentityAccess,
    Recordings,
    Registry,
    Analysis,
    AgentOrchestration
}
