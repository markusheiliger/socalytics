namespace SocAlytics.Platform.Persistence;

/// <summary>
/// Local/test bootstrap of one NOLOGIN owner role and one NOLOGIN runtime role per adopted module.
/// Roles are cluster-wide, so creation is idempotent and tolerates concurrent creators. Schema ownership
/// and grants are applied by each module's own migration; this only guarantees the roles exist.
/// </summary>
internal static class RoleBootstrap
{
    internal static string Sql { get; } = Build();

    private static string Build()
    {
        var statements = new System.Text.StringBuilder("DO $bootstrap$ BEGIN\n");
        foreach (var module in PersistenceModuleKey.All)
        {
            foreach (var role in new[] { module.OwnerRoleName, module.RuntimeRoleName })
            {
                // Names derive only from the fixed module keys, never from caller input.
                statements.Append(
                    $"IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '{role}') THEN\n" +
                    $"  BEGIN CREATE ROLE \"{role}\" NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT;\n" +
                    "  EXCEPTION WHEN duplicate_object OR unique_violation THEN NULL; END;\n" +
                    "END IF;\n");
            }
        }

        statements.Append("END $bootstrap$;");
        return statements.ToString();
    }
}
