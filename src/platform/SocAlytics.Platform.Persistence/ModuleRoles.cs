using Dapper;
using Npgsql;

namespace SocAlytics.Platform.Persistence;

/// <summary>
/// Local/test owner and runtime role semantics. Each adopted module has a NOLOGIN owner role
/// that owns only its schema and migration objects, and a NOLOGIN runtime role that receives
/// only usage and object privileges on that schema. Public and peer-module access is revoked.
/// </summary>
internal static class ModuleRoles
{
    public static string OwnerRoleName(ModuleKey moduleKey) => $"socalytics_{moduleKey.ToSchemaName()}_owner";

    public static string RuntimeRoleName(ModuleKey moduleKey) => $"socalytics_{moduleKey.ToSchemaName()}_runtime";

    public static void Bootstrap(NpgsqlConnection connection, string? runtimeLogin)
    {
        using var transaction = connection.BeginTransaction();
        connection.Execute("SELECT pg_advisory_xact_lock(hashtext('socalytics_module_roles'))", transaction: transaction);

        foreach (var moduleKey in Enum.GetValues<ModuleKey>())
        {
            var schema = Quote(moduleKey.ToSchemaName());
            var owner = Quote(OwnerRoleName(moduleKey));
            var runtime = Quote(RuntimeRoleName(moduleKey));

            foreach (var role in new[] { OwnerRoleName(moduleKey), RuntimeRoleName(moduleKey) })
            {
                if (connection.ExecuteScalar<int>("SELECT count(*) FROM pg_roles WHERE rolname = @role", new { role }, transaction) == 0)
                {
                    connection.Execute($"CREATE ROLE {Quote(role)} NOLOGIN", transaction: transaction);
                }
            }

            connection.Execute($"""
                CREATE SCHEMA IF NOT EXISTS {schema} AUTHORIZATION {owner};
                ALTER SCHEMA {schema} OWNER TO {owner};
                REVOKE ALL ON SCHEMA {schema} FROM PUBLIC;
                GRANT USAGE ON SCHEMA {schema} TO {runtime};
                ALTER DEFAULT PRIVILEGES FOR ROLE {owner} IN SCHEMA {schema} GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO {runtime};
                ALTER DEFAULT PRIVILEGES FOR ROLE {owner} IN SCHEMA {schema} GRANT USAGE, SELECT, UPDATE ON SEQUENCES TO {runtime};
                ALTER DEFAULT PRIVILEGES FOR ROLE {owner} IN SCHEMA {schema} GRANT EXECUTE ON FUNCTIONS TO {runtime};
                """, transaction: transaction);

            // The runtime login may only assume each runtime role explicitly; it never inherits
            // any module's privileges implicitly.
            if (!string.IsNullOrEmpty(runtimeLogin) &&
                connection.ExecuteScalar<bool>("SELECT @runtimeLogin <> current_user", new { runtimeLogin }, transaction))
            {
                connection.Execute($"GRANT {runtime} TO {Quote(runtimeLogin)} WITH INHERIT FALSE, SET TRUE", transaction: transaction);
            }
        }

        connection.Execute("REVOKE ALL ON SCHEMA socalytics_migrations FROM PUBLIC", transaction: transaction);
        transaction.Commit();
    }

    public static string Quote(string identifier) => $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
}
