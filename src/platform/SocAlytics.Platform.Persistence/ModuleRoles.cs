namespace SocAlytics.Platform.Persistence;

internal static class ModuleRoles
{
    public static string Owner(PersistenceModuleIdentity module) => $"{module.Key}_owner";

    public static string Runtime(PersistenceModuleIdentity module) => $"{module.Key}_runtime";

    public static string QuoteIdentifier(string name) => $"\"{name.Replace("\"", "\"\"")}\"";

    // Module keys are the fixed adopted identities, so the generated SQL never contains caller-supplied text.
    public static string BootstrapSql()
    {
        var sql = new System.Text.StringBuilder();
        sql.AppendLine("SELECT pg_advisory_xact_lock(hashtext('socalytics_role_bootstrap'));");
        sql.AppendLine("REVOKE CREATE ON SCHEMA public FROM PUBLIC;");
        foreach (var module in PersistenceModuleIdentity.All)
        {
            var owner = QuoteIdentifier(Owner(module));
            var runtime = QuoteIdentifier(Runtime(module));
            foreach (var (role, name) in new[] { (owner, Owner(module)), (runtime, Runtime(module)) })
            {
                sql.AppendLine(
                    $"""
                    DO $bootstrap$
                    BEGIN
                        IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '{name}') THEN
                            CREATE ROLE {role} NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS;
                        END IF;
                        IF NOT pg_has_role(current_user, '{name}', 'MEMBER') THEN
                            GRANT {role} TO CURRENT_USER;
                        END IF;
                    END
                    $bootstrap$;
                    """);
            }

            sql.AppendLine(
                $"""
                GRANT CREATE ON DATABASE {QuoteIdentifier("__DATABASE__")} TO {owner};
                ALTER DEFAULT PRIVILEGES FOR ROLE {owner} GRANT USAGE ON SCHEMAS TO {runtime};
                ALTER DEFAULT PRIVILEGES FOR ROLE {owner} GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO {runtime};
                ALTER DEFAULT PRIVILEGES FOR ROLE {owner} GRANT USAGE, SELECT, UPDATE ON SEQUENCES TO {runtime};
                ALTER DEFAULT PRIVILEGES FOR ROLE {owner} REVOKE EXECUTE ON FUNCTIONS FROM PUBLIC;
                ALTER DEFAULT PRIVILEGES FOR ROLE {owner} GRANT EXECUTE ON FUNCTIONS TO {runtime};
                """);
        }

        return sql.ToString();
    }
}
