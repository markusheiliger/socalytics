using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using SocAlytics.Platform.Persistence;
using Testcontainers.PostgreSql;
using Xunit;

namespace SocAlytics.Platform.Persistence.Tests;

public sealed class DatabaseRoleTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _database = new PostgreSqlBuilder("postgres:17-alpine").Build();

    public async ValueTask InitializeAsync() => await _database.StartAsync(TestContext.Current.CancellationToken);

    public async ValueTask DisposeAsync() => await _database.DisposeAsync();

    [Fact]
    public async Task Each_runtime_role_is_limited_to_its_owned_schema_and_denied_writes_are_rolled_back()
    {
        var services = new ServiceCollection();
        services.AddPlatformPersistence(_database.GetConnectionString(), _database.GetConnectionString());
        foreach (var module in PersistenceModuleIdentity.All)
        {
            services.AddModuleDatabaseConnections(module);
        }

        await using var provider = services.BuildServiceProvider();
        await provider.MigratePlatformDatabaseAsync(TestContext.Current.CancellationToken);
        await CreateModuleSchemasAsync();
        await provider.MigratePlatformDatabaseAsync(TestContext.Current.CancellationToken);

        foreach (var module in PersistenceModuleIdentity.All)
        {
            (await SchemaOwnerAsync(module)).ShouldBe(DatabaseRoleNamesForTest.Owner(module));
            (await OwnedSchemasAsync(module)).ShouldBe([module.Key]);
            await SchemaPrivilegesAsync(module);

            var factory = provider.GetRequiredKeyedService<IRuntimeDatabaseConnectionFactory>(module.Key);
            await using var connection = await factory.OpenConnectionAsync(TestContext.Current.CancellationToken);
            await using (var ownWrite = new NpgsqlCommand(
                $"INSERT INTO {Quote(module.Key)}.access_probe (value) VALUES ('runtime')",
                connection))
            {
                await ownWrite.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }

            foreach (var peer in PersistenceModuleIdentity.All.Where(peer => peer != module))
            {
                var peerCount = await CountAsync(peer);
                var readError = await Should.ThrowAsync<PostgresException>(async () =>
                {
                    await using var command = new NpgsqlCommand(
                        $"SELECT value FROM {Quote(peer.Key)}.access_probe",
                        connection);
                    await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
                });
                readError.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);

                await using (var transaction = await connection.BeginTransactionAsync(TestContext.Current.CancellationToken))
                {
                    await using (var ownWrite = new NpgsqlCommand(
                        $"INSERT INTO {Quote(module.Key)}.access_probe (value) VALUES ('rolled-back')",
                        connection,
                        transaction))
                    {
                        await ownWrite.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
                    }

                    var writeError = await Should.ThrowAsync<PostgresException>(async () =>
                    {
                        await using var command = new NpgsqlCommand(
                            $"INSERT INTO {Quote(peer.Key)}.access_probe (value) VALUES ('unauthorized')",
                            connection,
                            transaction);
                        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
                    });
                    writeError.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
                    await transaction.RollbackAsync(TestContext.Current.CancellationToken);
                }

                (await CountAsync(module)).ShouldBe(2);
                (await CountAsync(peer)).ShouldBe(peerCount);
            }
        }
    }

    private async Task CreateModuleSchemasAsync()
    {
        await using var connection = await OpenAdminConnectionAsync();
        foreach (var module in PersistenceModuleIdentity.All)
        {
            var schema = Quote(module.Key);
            var owner = Quote(DatabaseRoleNamesForTest.Owner(module));
            await using var command = new NpgsqlCommand(
                $"""
                CREATE SCHEMA {schema} AUTHORIZATION {owner};
                SET ROLE {owner};
                CREATE TABLE {schema}.access_probe (value text NOT NULL);
                INSERT INTO {schema}.access_probe (value) VALUES ('initial');
                RESET ROLE;
                """,
                connection);
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
    }

    private async Task SchemaPrivilegesAsync(PersistenceModuleIdentity module)
    {
        await using var connection = await OpenAdminConnectionAsync();
        foreach (var candidate in PersistenceModuleIdentity.All)
        {
            await using var command = new NpgsqlCommand(
                """
                SELECT has_schema_privilege(@owner, @schema, 'USAGE'),
                    has_schema_privilege(@runtime, @schema, 'USAGE'),
                    has_database_privilege(@owner, current_database(), 'CREATE'),
                    has_database_privilege(@runtime, current_database(), 'CREATE')
                """,
                connection);
            command.Parameters.AddWithValue("owner", DatabaseRoleNamesForTest.Owner(module));
            command.Parameters.AddWithValue("runtime", DatabaseRoleNamesForTest.Runtime(module));
            command.Parameters.AddWithValue("schema", candidate.Key);
            await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
            (await reader.ReadAsync(TestContext.Current.CancellationToken)).ShouldBeTrue();
            reader.GetBoolean(0).ShouldBe(candidate == module);
            reader.GetBoolean(1).ShouldBe(candidate == module);
            reader.GetBoolean(2).ShouldBeFalse();
            reader.GetBoolean(3).ShouldBeFalse();
        }
    }

    private async Task<string[]> OwnedSchemasAsync(PersistenceModuleIdentity module)
    {
        await using var connection = await OpenAdminConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT array_agg(schema.nspname ORDER BY schema.nspname)
            FROM pg_namespace schema
            JOIN pg_roles role ON role.oid = schema.nspowner
            WHERE role.rolname = @owner
            """,
            connection);
        command.Parameters.AddWithValue("owner", DatabaseRoleNamesForTest.Owner(module));
        return (string[])(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    private async Task<string> SchemaOwnerAsync(PersistenceModuleIdentity module)
    {
        await using var connection = await OpenAdminConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT role.rolname
            FROM pg_namespace schema
            JOIN pg_roles role ON role.oid = schema.nspowner
            WHERE schema.nspname = @schema
            """,
            connection);
        command.Parameters.AddWithValue("schema", module.Key);
        return (string)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    private async Task<long> CountAsync(PersistenceModuleIdentity module)
    {
        await using var connection = await OpenAdminConnectionAsync();
        await using var command = new NpgsqlCommand(
            $"SELECT count(*) FROM {Quote(module.Key)}.access_probe",
            connection);
        return (long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    private async Task<NpgsqlConnection> OpenAdminConnectionAsync()
    {
        var connection = new NpgsqlConnection(_database.GetConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        return connection;
    }

    private static string Quote(string identifier) => new NpgsqlCommandBuilder().QuoteIdentifier(identifier);

    private static class DatabaseRoleNamesForTest
    {
        public static string Owner(PersistenceModuleIdentity module) => $"socalytics_{module.Key}_owner";

        public static string Runtime(PersistenceModuleIdentity module) => $"socalytics_{module.Key}_runtime";
    }
}
