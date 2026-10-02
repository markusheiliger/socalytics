using Npgsql;

namespace SocAlytics.Platform.Persistence;

internal static class DatabaseRoleNames
{
    public static string Owner(PersistenceModuleIdentity module) => $"socalytics_{module.Key}_owner";

    public static string Runtime(PersistenceModuleIdentity module) => $"socalytics_{module.Key}_runtime";
}

internal sealed class DatabaseRoleBootstrapper(IBootstrapDatabaseConnectionFactory connections)
{
    public async Task BootstrapAsync(CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenConnectionAsync(cancellationToken);

        foreach (var module in PersistenceModuleIdentity.All)
        {
            await EnsureRoleAsync(connection, DatabaseRoleNames.Owner(module), cancellationToken);
            await EnsureRoleAsync(connection, DatabaseRoleNames.Runtime(module), cancellationToken);
        }

        foreach (var module in PersistenceModuleIdentity.All)
        {
            await SetSchemaCreationPrivilegeAsync(connection, module, false, cancellationToken);
        }

        foreach (var module in PersistenceModuleIdentity.All)
        {
            await ConfigureSchemaAsync(connection, module, cancellationToken);
        }
    }

    public async Task SetSchemaCreationPrivilegeAsync(
        PersistenceModuleIdentity module,
        bool enabled,
        CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenConnectionAsync(cancellationToken);
        await SetSchemaCreationPrivilegeAsync(connection, module, enabled, cancellationToken);
    }

    private static async Task EnsureRoleAsync(
        NpgsqlConnection connection,
        string roleName,
        CancellationToken cancellationToken)
    {
        var quotedRole = new NpgsqlCommandBuilder().QuoteIdentifier(roleName);
        await using var existsCommand = new NpgsqlCommand(
            "SELECT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = @roleName)",
            connection);
        existsCommand.Parameters.AddWithValue("roleName", roleName);
        var exists = (bool)(await existsCommand.ExecuteScalarAsync(cancellationToken))!;

        var sql = exists
            ? $"ALTER ROLE {quotedRole} NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOLOGIN NOREPLICATION NOBYPASSRLS"
            : $"CREATE ROLE {quotedRole} NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOLOGIN NOREPLICATION NOBYPASSRLS";
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task ConfigureSchemaAsync(
        NpgsqlConnection connection,
        PersistenceModuleIdentity module,
        CancellationToken cancellationToken)
    {
        var schemaName = module.Key;
        await using var existsCommand = new NpgsqlCommand(
            "SELECT EXISTS (SELECT 1 FROM pg_namespace WHERE nspname = @schemaName)",
            connection);
        existsCommand.Parameters.AddWithValue("schemaName", schemaName);
        if (!(bool)(await existsCommand.ExecuteScalarAsync(cancellationToken))!)
        {
            return;
        }

        var identifierBuilder = new NpgsqlCommandBuilder();
        var schema = identifierBuilder.QuoteIdentifier(schemaName);
        var owner = identifierBuilder.QuoteIdentifier(DatabaseRoleNames.Owner(module));
        var runtime = identifierBuilder.QuoteIdentifier(DatabaseRoleNames.Runtime(module));
        var peerRoles = PersistenceModuleIdentity.All
            .Where(peer => peer != module)
            .SelectMany(peer => new[] { DatabaseRoleNames.Owner(peer), DatabaseRoleNames.Runtime(peer) })
            .Select(role => identifierBuilder.QuoteIdentifier(role))
            .Prepend("PUBLIC");
        var peerRoleList = string.Join(", ", peerRoles);

        var sql = $"""
            ALTER SCHEMA {schema} OWNER TO {owner};
            REVOKE ALL PRIVILEGES ON SCHEMA {schema} FROM {peerRoleList};
            GRANT USAGE ON SCHEMA {schema} TO {runtime};
            REVOKE ALL PRIVILEGES ON ALL TABLES IN SCHEMA {schema} FROM {peerRoleList};
            GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA {schema} TO {runtime};
            REVOKE ALL PRIVILEGES ON ALL SEQUENCES IN SCHEMA {schema} FROM {peerRoleList};
            GRANT USAGE, SELECT, UPDATE ON ALL SEQUENCES IN SCHEMA {schema} TO {runtime};
            REVOKE ALL PRIVILEGES ON ALL FUNCTIONS IN SCHEMA {schema} FROM {peerRoleList};
            ALTER DEFAULT PRIVILEGES FOR ROLE {owner} IN SCHEMA {schema}
                REVOKE ALL ON TABLES FROM {peerRoleList};
            ALTER DEFAULT PRIVILEGES FOR ROLE {owner} IN SCHEMA {schema}
                GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO {runtime};
            ALTER DEFAULT PRIVILEGES FOR ROLE {owner} IN SCHEMA {schema}
                REVOKE ALL ON SEQUENCES FROM {peerRoleList};
            ALTER DEFAULT PRIVILEGES FOR ROLE {owner} IN SCHEMA {schema}
                GRANT USAGE, SELECT, UPDATE ON SEQUENCES TO {runtime};
            ALTER DEFAULT PRIVILEGES FOR ROLE {owner} IN SCHEMA {schema}
                REVOKE EXECUTE ON FUNCTIONS FROM {peerRoleList};
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task SetSchemaCreationPrivilegeAsync(
        NpgsqlConnection connection,
        PersistenceModuleIdentity module,
        bool enabled,
        CancellationToken cancellationToken)
    {
        await using var databaseCommand = new NpgsqlCommand("SELECT current_database()", connection);
        var databaseName = (string)(await databaseCommand.ExecuteScalarAsync(cancellationToken))!;
        var database = new NpgsqlCommandBuilder().QuoteIdentifier(databaseName);
        var owner = new NpgsqlCommandBuilder().QuoteIdentifier(DatabaseRoleNames.Owner(module));
        var privilege = enabled ? "GRANT" : "REVOKE";
        var sql = enabled
            ? $"{privilege} CREATE ON DATABASE {database} TO {owner}"
            : $"{privilege} CREATE ON DATABASE {database} FROM {owner}";
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
