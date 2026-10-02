using System.Text;
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

    /// <summary>Creates a fresh database in the disposable container and returns its connection string.</summary>
    public async Task<string> CreateDatabaseAsync()
    {
        var name = "t_" + Guid.NewGuid().ToString("N");
        await using var connection = new NpgsqlConnection(_container.GetConnectionString());
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"CREATE DATABASE {name}", connection);
        await command.ExecuteNonQueryAsync();
        return new NpgsqlConnectionStringBuilder(_container.GetConnectionString()) { Database = name }.ConnectionString;
    }
}

public sealed class MigrationRunnerTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static MigrationDescriptor Script(PersistenceModuleKey module, int sequence, string name, string sql) =>
        new(module, sequence, name, Encoding.UTF8.GetBytes(sql));

    private static async Task<IMigrationRunner> RunnerAsync(string connectionString, params MigrationDescriptor[] migrations)
    {
        var services = new ServiceCollection();
        services.AddPlatformPersistence(o => o.BootstrapConnectionString = connectionString);
        foreach (var group in migrations.GroupBy(m => m.Module))
        {
            services.AddModuleMigrations(new Contributor(group.Key, group.ToArray()));
        }

        var provider = services.BuildServiceProvider();
        await Task.CompletedTask;
        return provider.GetRequiredService<IMigrationRunner>();
    }

    private static async Task<List<string>> HistoryAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT module_key || '/' || sequence || '/' || script_name FROM socalytics_migrations.history ORDER BY applied_at, module_key, sequence",
            connection);
        var rows = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(reader.GetString(0));
        }

        return rows;
    }

    private static async Task<bool> TableExistsAsync(string connectionString, string table)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT to_regclass(@t) IS NOT NULL", connection);
        command.Parameters.AddWithValue("t", table);
        return (bool)(await command.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task CleanDatabaseMigratesInModuleThenSequenceOrder()
    {
        var cs = await postgres.CreateDatabaseAsync();
        var runner = await RunnerAsync(cs,
            Script(PersistenceModuleKey.Registry, 1, "r1", "CREATE SCHEMA registry;"),
            Script(PersistenceModuleKey.Club, 2, "c2", "CREATE TABLE club.t (id int);"),
            Script(PersistenceModuleKey.Club, 1, "c1", "CREATE SCHEMA club;"));

        await runner.MigrateAsync();

        (await HistoryAsync(cs)).ShouldBe(["club/1/c1", "club/2/c2", "registry/1/r1"]);
        await using var connection = new NpgsqlConnection(cs);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT checksum FROM socalytics_migrations.history WHERE script_name = 'c1'", connection);
        ((string)(await command.ExecuteScalarAsync())!).Trim().ShouldBe(
            MigrationDescriptor.ComputeChecksum(Encoding.UTF8.GetBytes("CREATE SCHEMA club;")));
    }

    [Fact]
    public async Task UnchangedRepeatRunSkipsAppliedScripts()
    {
        var cs = await postgres.CreateDatabaseAsync();
        var scripts = new[] { Script(PersistenceModuleKey.Club, 1, "c1", "CREATE SCHEMA club; CREATE TABLE club.t (id int);") };

        await (await RunnerAsync(cs, scripts)).MigrateAsync();
        await (await RunnerAsync(cs, scripts)).MigrateAsync();

        (await HistoryAsync(cs)).ShouldBe(["club/1/c1"]);
    }

    [Fact]
    public async Task ChecksumConflictFailsBeforeAnyLaterWork()
    {
        var cs = await postgres.CreateDatabaseAsync();
        await (await RunnerAsync(cs, Script(PersistenceModuleKey.Club, 1, "c1", "CREATE SCHEMA club;"))).MigrateAsync();

        var runner = await RunnerAsync(cs,
            Script(PersistenceModuleKey.Club, 1, "c1", "CREATE SCHEMA club; -- edited"),
            Script(PersistenceModuleKey.Registry, 1, "r1", "CREATE SCHEMA registry;"));

        var ex = await Should.ThrowAsync<MigrationChecksumConflictException>(() => runner.MigrateAsync());

        ex.Module.ShouldBe("club");
        ex.ScriptName.ShouldBe("c1");
        ex.Message.ShouldNotContain("edited");
        (await HistoryAsync(cs)).ShouldBe(["club/1/c1"]);
        (await TableExistsAsync(cs, "registry.nothing")).ShouldBeFalse();
        await using var connection = new NpgsqlConnection(cs);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT count(*) FROM pg_namespace WHERE nspname = 'registry'", connection);
        ((long)(await command.ExecuteScalarAsync())!).ShouldBe(0);
    }

    [Fact]
    public async Task FailedScriptRollsBackWhileEarlierScriptsRemainCommitted()
    {
        var cs = await postgres.CreateDatabaseAsync();
        var runner = await RunnerAsync(cs,
            Script(PersistenceModuleKey.Club, 1, "c1", "CREATE SCHEMA club; CREATE TABLE club.ok (id int);"),
            Script(PersistenceModuleKey.Club, 2, "c2", "CREATE TABLE club.partial (id int); SELECT 1/0; -- secret-body"),
            Script(PersistenceModuleKey.Registry, 1, "r1", "CREATE SCHEMA registry;"));

        var ex = await Should.ThrowAsync<MigrationFailedException>(() => runner.MigrateAsync());

        ex.ScriptName.ShouldBe("c2");
        ex.Module.ShouldBe("club");
        ex.Message.ShouldNotContain("secret-body");
        ex.Message.ShouldNotContain("Password");
        ex.InnerException.ShouldBeNull();
        (await HistoryAsync(cs)).ShouldBe(["club/1/c1"]);
        (await TableExistsAsync(cs, "club.ok")).ShouldBeTrue();
        (await TableExistsAsync(cs, "club.partial")).ShouldBeFalse();
        (await TableExistsAsync(cs, "registry.anything")).ShouldBeFalse();
    }

    [Fact]
    public async Task UnreachableDatabaseFailsWithSanitizedMessage()
    {
        var cs = "Host=127.0.0.1;Port=1;Database=x;Username=u;Password=hunter2;Timeout=2";
        var runner = await RunnerAsync(cs, Script(PersistenceModuleKey.Club, 1, "c1", "CREATE SCHEMA club;"));

        var ex = await Should.ThrowAsync<MigrationFailedException>(() => runner.MigrateAsync());

        ex.Message.ShouldNotContain("hunter2");
    }

    private sealed class Contributor(PersistenceModuleKey module, IReadOnlyList<MigrationDescriptor> migrations)
        : IMigrationContributor
    {
        public PersistenceModuleKey Module { get; } = module;

        public IReadOnlyList<MigrationDescriptor> GetMigrations() => migrations;
    }
}
