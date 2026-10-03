using System.Text;
using Npgsql;

namespace SocAlytics.Platform.Persistence;

/// <summary>
/// Creates the local/test NOLOGIN owner and runtime roles for every adopted module and fixes their
/// schema privileges. Role and schema names derive only from the fixed module identities, so the
/// interpolated identifiers are never caller-controlled. The statement is idempotent.
/// </summary>
internal static class RoleBootstrap
{
    public static async Task EnsureAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(BuildSql(), connection);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static string BuildSql()
    {
        var sql = new StringBuilder();
        sql.AppendLine($"REVOKE ALL ON SCHEMA {MigrationJournal.Schema} FROM PUBLIC;");
        foreach (var module in ModuleKey.All)
        {
            sql.AppendLine($$"""
                DO $$
                BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '{{module.OwnerRole}}') THEN
                        CREATE ROLE "{{module.OwnerRole}}" NOLOGIN;
                    END IF;
                    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '{{module.RuntimeRole}}') THEN
                        CREATE ROLE "{{module.RuntimeRole}}" NOLOGIN;
                    END IF;
                END
                $$;
                CREATE SCHEMA IF NOT EXISTS "{{module.Schema}}" AUTHORIZATION "{{module.OwnerRole}}";
                REVOKE ALL ON SCHEMA "{{module.Schema}}" FROM PUBLIC;
                GRANT USAGE ON SCHEMA "{{module.Schema}}" TO "{{module.RuntimeRole}}";
                ALTER DEFAULT PRIVILEGES FOR ROLE "{{module.OwnerRole}}" IN SCHEMA "{{module.Schema}}"
                    GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO "{{module.RuntimeRole}}";
                ALTER DEFAULT PRIVILEGES FOR ROLE "{{module.OwnerRole}}" IN SCHEMA "{{module.Schema}}"
                    GRANT USAGE, SELECT ON SEQUENCES TO "{{module.RuntimeRole}}";
                ALTER DEFAULT PRIVILEGES FOR ROLE "{{module.OwnerRole}}" IN SCHEMA "{{module.Schema}}"
                    GRANT EXECUTE ON FUNCTIONS TO "{{module.RuntimeRole}}";
                """);
        }

        // Runtime roles must never reach a peer module through a role membership or the shared schema.
        foreach (var module in ModuleKey.All)
        {
            foreach (var peer in ModuleKey.All.Where(p => p != module))
            {
                sql.AppendLine($"REVOKE ALL ON SCHEMA \"{peer.Schema}\" FROM \"{module.RuntimeRole}\", \"{module.OwnerRole}\";");
            }
        }

        return sql.ToString();
    }
}
