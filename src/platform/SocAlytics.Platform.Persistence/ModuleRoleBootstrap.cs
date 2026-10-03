using System.Text;

namespace SocAlytics.Platform.Persistence;

/// <summary>
/// Builds the idempotent local/test role bootstrap. Every identifier derives from the closed
/// <see cref="PersistenceModule"/> set, never from caller input. Production identities and
/// credentials are intentionally out of scope.
/// </summary>
internal static class ModuleRoleBootstrap
{
    public static string BuildSql()
    {
        var sql = new StringBuilder();

        foreach (var module in PersistenceModule.All)
        {
            var owner = module.OwnerRole;
            var runtime = module.RuntimeRole;

            sql.AppendLine($$"""
                DO $bootstrap$
                BEGIN
                    BEGIN CREATE ROLE "{{owner}}" NOLOGIN NOINHERIT; EXCEPTION WHEN duplicate_object OR unique_violation THEN NULL; END;
                    BEGIN CREATE ROLE "{{runtime}}" NOLOGIN NOINHERIT; EXCEPTION WHEN duplicate_object OR unique_violation THEN NULL; END;
                    IF NOT pg_has_role(current_user, '{{owner}}', 'MEMBER') THEN GRANT "{{owner}}" TO CURRENT_USER; END IF;
                    IF NOT pg_has_role(current_user, '{{runtime}}', 'MEMBER') THEN GRANT "{{runtime}}" TO CURRENT_USER; END IF;
                    EXECUTE format('GRANT CREATE ON DATABASE %I TO "{{owner}}"', current_database());
                END
                $bootstrap$;
                ALTER DEFAULT PRIVILEGES FOR ROLE "{{owner}}" GRANT USAGE ON SCHEMAS TO "{{runtime}}";
                ALTER DEFAULT PRIVILEGES FOR ROLE "{{owner}}" GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO "{{runtime}}";
                ALTER DEFAULT PRIVILEGES FOR ROLE "{{owner}}" GRANT USAGE, SELECT, UPDATE ON SEQUENCES TO "{{runtime}}";
                ALTER DEFAULT PRIVILEGES FOR ROLE "{{owner}}" REVOKE EXECUTE ON FUNCTIONS FROM PUBLIC;
                ALTER DEFAULT PRIVILEGES FOR ROLE "{{owner}}" GRANT EXECUTE ON FUNCTIONS TO "{{runtime}}";
                """);
        }

        return sql.ToString();
    }

    /// <summary>Runs the migration as the owning module's owner role; history is written after the role is reset.</summary>
    public static string WrapMigration(PersistenceModule module, string script) =>
        $"SET LOCAL ROLE \"{module.OwnerRole}\";\n{script}\nRESET ROLE;";
}
