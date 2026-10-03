#pragma warning disable xUnit1051 // Tests are short-lived; cancellation is not exercised here.
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using Xunit;

namespace SocAlytics.Platform.Persistence.Tests;

public sealed class ModuleIsolationTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static readonly PersistenceModule Club = PersistenceModule.Club;
    private static readonly PersistenceModule Registry = PersistenceModule.Registry;

    [Fact]
    public async Task Each_module_schema_is_owned_by_its_owner_role_and_runtime_role_is_not_an_owner()
    {
        await using var host = await MigrateAsync();

        var owners = await QueryAsync(host.ConnectionString,
            "SELECT nspname, pg_get_userbyid(nspowner) FROM pg_namespace WHERE nspname IN ('club','registry') ORDER BY nspname");
        owners.Select(r => $"{r[0]}:{r[1]}").ShouldBe(["club:club_owner", "registry:registry_owner"]);

        var tables = await QueryAsync(host.ConnectionString,
            "SELECT schemaname, tableowner FROM pg_tables WHERE schemaname IN ('club','registry') ORDER BY schemaname");
        tables.Select(r => $"{r[0]}:{r[1]}").ShouldBe(["club:club_owner", "registry:registry_owner"]);
    }

    [Fact]
    public async Task Module_session_reads_and_writes_only_its_own_schema()
    {
        await using var host = await MigrateAsync();

        await using var connection = await host.Factory(Club).OpenConnectionAsync();
        await ExecuteAsync(connection, "INSERT INTO club.item (n) VALUES (1)");
        (await ScalarAsync(connection, "SELECT current_user")).ShouldBe("club_runtime");
        (await ScalarAsync(connection, "SELECT count(*) FROM club.item")).ShouldBe(1L);
    }

    [Theory]
    [InlineData("SELECT count(*) FROM registry.item")]
    [InlineData("INSERT INTO registry.item (n) VALUES (99)")]
    [InlineData("UPDATE registry.item SET n = 99")]
    [InlineData("DELETE FROM registry.item")]
    [InlineData("CREATE TABLE registry.intruder (n int)")]
    [InlineData("CREATE TABLE club.intruder (n int)")]
    [InlineData("CREATE SCHEMA intruder")]
    public async Task Cross_schema_and_ddl_access_is_denied_and_commits_nothing(string sql)
    {
        await using var host = await MigrateAsync();

        await using (var connection = await host.Factory(Club).OpenConnectionAsync())
        {
            await using var transaction = await connection.BeginTransactionAsync();
            var exception = await Should.ThrowAsync<PostgresException>(() => ExecuteAsync(connection, sql, transaction));
            exception.SqlState.ShouldBe("42501");
        }

        (await QueryAsync(host.ConnectionString, "SELECT n FROM registry.item")).Count.ShouldBe(1);
        (await QueryAsync(host.ConnectionString, "SELECT 1 FROM pg_namespace WHERE nspname = 'intruder'")).ShouldBeEmpty();
        (await QueryAsync(host.ConnectionString,
            "SELECT 1 FROM pg_tables WHERE tablename = 'intruder'")).ShouldBeEmpty();
    }

    [Fact]
    public async Task Failed_cross_schema_write_does_not_commit_earlier_work_in_the_same_transaction()
    {
        await using var host = await MigrateAsync();

        await using (var connection = await host.Factory(Club).OpenConnectionAsync())
        {
            await using var transaction = await connection.BeginTransactionAsync();
            await ExecuteAsync(connection, "INSERT INTO club.item (n) VALUES (7)", transaction);
            await Should.ThrowAsync<PostgresException>(() => ExecuteAsync(connection, "INSERT INTO registry.item (n) VALUES (7)", transaction));
        }

        (await QueryAsync(host.ConnectionString, "SELECT n FROM club.item WHERE n = 7")).ShouldBeEmpty();
    }

    [Fact]
    public async Task Role_bootstrap_is_repeatable()
    {
        await using var host = await MigrateAsync();
        await host.Provider.GetRequiredService<IMigrationRunner>().RunAsync();

        var roles = await QueryAsync(host.ConnectionString,
            "SELECT rolname FROM pg_roles WHERE rolname NOT LIKE 'pg\\_%' AND (rolname LIKE '%\\_owner' OR rolname LIKE '%\\_runtime')");
        roles.Count.ShouldBe(12);
    }

    private async Task<Host> MigrateAsync()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        var services = new ServiceCollection();
        services.AddSingleton(NpgsqlDataSource.Create(connectionString));
        services.AddModulePersistence(Club, new Contributor(Club, "CREATE SCHEMA club; CREATE TABLE club.item (n int);"));
        services.AddModulePersistence(Registry, new Contributor(Registry, "CREATE SCHEMA registry; CREATE TABLE registry.item (n int); INSERT INTO registry.item VALUES (1);"));

        var provider = services.BuildServiceProvider();
        await provider.GetRequiredService<IMigrationRunner>().RunAsync();
        return new Host(provider, connectionString);
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, NpgsqlTransaction? transaction = null)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<object?> ScalarAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteScalarAsync();
    }

    private static async Task<List<object?[]>> QueryAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();

        var rows = new List<object?[]>();
        while (await reader.ReadAsync())
        {
            var row = new object?[reader.FieldCount];
            reader.GetValues(row!);
            rows.Add(row);
        }

        return rows;
    }

    private sealed class Host(ServiceProvider provider, string connectionString) : IAsyncDisposable
    {
        public ServiceProvider Provider { get; } = provider;

        public string ConnectionString { get; } = connectionString;

        public IModuleConnectionFactory Factory(PersistenceModule module) =>
            Provider.GetRequiredKeyedService<IModuleConnectionFactory>(module.Key);

        public ValueTask DisposeAsync() => Provider.DisposeAsync();
    }

    private sealed class Contributor(PersistenceModule module, string sql) : IMigrationContributor
    {
        public PersistenceModule Module { get; } = module;

        public IEnumerable<MigrationDescriptor> GetMigrations() =>
            [new(Module, 1, "initial", System.Text.Encoding.UTF8.GetBytes(sql))];
    }
}
