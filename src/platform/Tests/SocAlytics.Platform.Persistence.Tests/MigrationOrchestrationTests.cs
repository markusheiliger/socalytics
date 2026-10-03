using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using Testcontainers.PostgreSql;
using Xunit;

namespace SocAlytics.Platform.Persistence.Tests;

public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine").Build();

    public async ValueTask InitializeAsync() => await _container.StartAsync();

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    /// <summary>Creates a fresh database so tests never share migration history.</summary>
    public async Task<string> CreateDatabaseAsync()
    {
        var name = "t_" + Guid.NewGuid().ToString("N");
        await using var connection = new NpgsqlConnection(_container.GetConnectionString());
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"CREATE DATABASE {name}", connection);
        await command.ExecuteNonQueryAsync();

        return new NpgsqlConnectionStringBuilder(_container.GetConnectionString()) { Database = name, Pooling = false }.ConnectionString;
    }
}

public sealed class MigrationOrchestrationTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task Clean_database_migrates_in_module_then_sequence_order_with_complete_history()
    {
        var connectionString = await postgres.CreateDatabaseAsync();

        await RunAsync(connectionString,
            Contributor(PersistenceModule.Registry, Script(PersistenceModule.Registry, 1, "r1", "CREATE SCHEMA r; CREATE TABLE r.t (n int);")),
            Contributor(PersistenceModule.Club,
                Script(PersistenceModule.Club, 2, "c2", "CREATE TABLE c.t (n int);"),
                Script(PersistenceModule.Club, 1, "c1", "CREATE SCHEMA c;")));

        var rows = await QueryAsync(connectionString,
            "SELECT module, sequence, script_identity, checksum FROM socalytics_migrations.history ORDER BY applied_at, sequence");
        rows.Select(r => $"{r[0]}:{r[1]}:{r[2]}").ShouldBe(["club:1:c1", "club:2:c2", "registry:1:r1"]);
        rows.Select(r => (string)r[3]!).ShouldAllBe(c => c.Length == 64);
        rows[0][3].ShouldBe(Script(PersistenceModule.Club, 1, "c1", "CREATE SCHEMA c;").Checksum);
    }

    [Fact]
    public async Task Repeat_run_skips_applied_scripts_and_leaves_history_unchanged()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        IMigrationContributor[] contributors =
        [
            Contributor(PersistenceModule.Club, Script(PersistenceModule.Club, 1, "c1", "CREATE SCHEMA c; CREATE TABLE c.t (n int);")),
        ];

        await RunAsync(connectionString, contributors);
        var before = await QueryAsync(connectionString, "SELECT module, sequence, script_identity, checksum, applied_at FROM socalytics_migrations.history");

        await RunAsync(connectionString, contributors);
        var after = await QueryAsync(connectionString, "SELECT module, sequence, script_identity, checksum, applied_at FROM socalytics_migrations.history");

        after.Count.ShouldBe(1);
        after[0].ShouldBe(before[0]);
    }

    [Fact]
    public async Task Checksum_conflict_fails_before_any_later_migration_runs()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        await RunAsync(connectionString,
            Contributor(PersistenceModule.Club, Script(PersistenceModule.Club, 1, "c1", "CREATE SCHEMA c;")));

        var exception = await Should.ThrowAsync<MigrationException>(() => RunAsync(connectionString,
            Contributor(PersistenceModule.Club, Script(PersistenceModule.Club, 1, "c1", "CREATE SCHEMA c; -- edited")),
            Contributor(PersistenceModule.Registry, Script(PersistenceModule.Registry, 1, "r1", "CREATE SCHEMA r;"))));

        exception.Kind.ShouldBe(MigrationFailureKind.ChecksumConflict);
        exception.Module.ShouldBe("club");
        exception.Identity.ShouldBe("c1");
        exception.Message.ShouldNotContain("edited");

        (await QueryAsync(connectionString, "SELECT 1 FROM information_schema.schemata WHERE schema_name = 'r'")).ShouldBeEmpty();
        (await QueryAsync(connectionString, "SELECT 1 FROM socalytics_migrations.history")).Count.ShouldBe(1);
    }

    [Fact]
    public async Task Failed_script_rolls_back_without_history_and_keeps_earlier_scripts()
    {
        var connectionString = await postgres.CreateDatabaseAsync();

        var exception = await Should.ThrowAsync<MigrationException>(() => RunAsync(connectionString,
            Contributor(PersistenceModule.Club,
                Script(PersistenceModule.Club, 1, "c1", "CREATE SCHEMA c;"),
                Script(PersistenceModule.Club, 2, "c2", "CREATE TABLE c.partial (n int); INSERT INTO c.missing VALUES ('secret-value');"),
                Script(PersistenceModule.Club, 3, "c3", "CREATE TABLE c.later (n int);"))));

        exception.Kind.ShouldBe(MigrationFailureKind.ScriptFailed);
        exception.Module.ShouldBe("club");
        exception.Identity.ShouldBe("c2");
        exception.Message.ShouldNotContain("secret-value");
        exception.Message.ShouldNotContain("INSERT");

        var history = await QueryAsync(connectionString, "SELECT script_identity FROM socalytics_migrations.history");
        history.Select(r => r[0]).ShouldBe(["c1"]);
        (await QueryAsync(connectionString, "SELECT 1 FROM information_schema.schemata WHERE schema_name = 'c'")).Count.ShouldBe(1);
        (await QueryAsync(connectionString, "SELECT 1 FROM information_schema.tables WHERE table_schema = 'c'")).ShouldBeEmpty();
    }

    private static async Task RunAsync(string connectionString, params IMigrationContributor[] contributors)
    {
        var services = new ServiceCollection();
        services.AddSingleton(NpgsqlDataSource.Create(connectionString));
        foreach (var contributor in contributors)
        {
            services.AddModulePersistence(contributor.Module, contributor);
        }

        await using var provider = services.BuildServiceProvider();
        await provider.GetRequiredService<IMigrationRunner>().RunAsync();
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

    private static MigrationDescriptor Script(PersistenceModule module, int sequence, string identity, string sql) =>
        new(module, sequence, identity, System.Text.Encoding.UTF8.GetBytes(sql));

    private static IMigrationContributor Contributor(PersistenceModule module, params MigrationDescriptor[] migrations) =>
        new TestContributor(module, migrations);

    private sealed class TestContributor(PersistenceModule module, MigrationDescriptor[] migrations) : IMigrationContributor
    {
        public PersistenceModule Module { get; } = module;

        public IEnumerable<MigrationDescriptor> GetMigrations() => migrations;
    }
}
