namespace SocAlytics.Platform.Persistence;

/// <summary>
/// Maps each fixed <see cref="ModuleKey"/> to its adopted, immutable PostgreSQL schema name.
/// </summary>
public static class ModuleKeyExtensions
{
    private static readonly IReadOnlyDictionary<ModuleKey, string> SchemaNamesByModuleKey = new Dictionary<ModuleKey, string>
    {
        [ModuleKey.Club] = "club",
        [ModuleKey.IdentityAccess] = "identity_access",
        [ModuleKey.Recordings] = "recordings",
        [ModuleKey.Registry] = "registry",
        [ModuleKey.Analysis] = "analysis",
        [ModuleKey.AgentOrchestration] = "agent_orchestration"
    };

    public static string ToSchemaName(this ModuleKey moduleKey)
    {
        if (!SchemaNamesByModuleKey.TryGetValue(moduleKey, out var schemaName))
        {
            throw new ArgumentOutOfRangeException(nameof(moduleKey), moduleKey, "The module key is not an adopted platform module.");
        }

        return schemaName;
    }
}
