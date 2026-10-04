using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using Testcontainers.PostgreSql;
using Xunit;

namespace SocAlytics.Platform.Persistence.Tests;

public sealed class MigrationOrchestrationTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine").Build();
    private int _databases;

    public async ValueTask InitializeAsync() => await _container.StartAsync();

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    private sealed class Contributor(PersistenceModuleKey module, params MigrationDescriptor[] migrations)
        : IMigrationContributor
    {
        public PersistenceModuleKey Module { get; } = module;

        public IReadOnlyList<MigrationDescriptor> GetMigrations() => migrations;
    }

    private static MigrationDescriptor Script(PersistenceModuleKey module, int sequence, string identity, string sql) =>
        new(module, sequence, identity, Encoding.UTF8.GetBytes(sql));

    private async Task<string> CreateDatabaseAsync()
    {
        var name = $"migrations_{Interlocked.Increment(ref _databases)}";
        await using var admin = new NpgsqlConnection(_container.GetConnectionString());
        await admin.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = admin.CreateCommand();
        command.CommandText = $"CREATE DATABASE {name}";
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);

        return new NpgsqlConnectionStringBuilder(_container.GetConnectionString()) { Database = name, Pooling = false }
            .ConnectionString;
    }

    private static MigrationOrchestrator Orchestrator(string connectionString, params IMigrationContributor[] contributors)
    {
        var services = new ServiceCollection().AddPlatformPersistence(connectionString);
        foreach (var contributor in contributors)
        {
            services.AddModulePersistence(contributor);
        }

        return services.BuildServiceProvider().GetRequiredService<MigrationOrchestrator>();
    }

    private static async Task<List<string>> ScalarsAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        var values = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            values.Add(reader.GetValue(0).ToString()!);
        }

        return values;
    }

    private const string HistoryQuery =
        "SELECT module || '/' || sequence || '/' || identity || '/' || checksum FROM socalytics_migrations.history ORDER BY module, sequence";

    [Fact]
    public async Task CleanDatabaseMigratesInModuleAndSequenceOrderWithHistory()
    {
        var connectionString = await CreateDatabaseAsync();
        var club = new Contributor(PersistenceModuleKey.Club,
            Script(PersistenceModuleKey.Club, 2, "0002", "CREATE TABLE club_two (id int);"),
            Script(PersistenceModuleKey.Club, 1, "0001", "CREATE TABLE club_one (id int);"));
        var registry = new Contributor(PersistenceModuleKey.Registry,
            Script(PersistenceModuleKey.Registry, 1, "0001", "CREATE TABLE registry_one (id int);"));

        await Orchestrator(connectionString, registry, club).MigrateAsync(TestContext.Current.CancellationToken);

        var history = await ScalarsAsync(connectionString, HistoryQuery);
        history.Select(h => string.Join('/', h.Split('/').Take(3)))
            .ShouldBe(["club/1/0001", "club/2/0002", "registry/1/0001"]);
        history[0].ShouldEndWith(Script(PersistenceModuleKey.Club, 1, "0001", "CREATE TABLE club_one (id int);").Checksum);
        (await ScalarsAsync(connectionString,
            "SELECT count(*) FROM information_schema.tables WHERE table_name IN ('club_one','club_two','registry_one')"))
            .ShouldBe(["3"]);
    }

    [Fact]
    public async Task UnchangedRepeatRunLeavesHistoryUnchanged()
    {
        var connectionString = await CreateDatabaseAsync();
        var contributor = new Contributor(PersistenceModuleKey.Club,
            Script(PersistenceModuleKey.Club, 1, "0001", "CREATE TABLE club_one (id int);"));
        var orchestrator = Orchestrator(connectionString, contributor);

        await orchestrator.MigrateAsync(TestContext.Current.CancellationToken);
        var first = await ScalarsAsync(connectionString,
            "SELECT identity || applied_at::text FROM socalytics_migrations.history");
        await orchestrator.MigrateAsync(TestContext.Current.CancellationToken);

        (await ScalarsAsync(connectionString, "SELECT identity || applied_at::text FROM socalytics_migrations.history"))
            .ShouldBe(first);
    }

    [Fact]
    public async Task ChecksumConflictFailsBeforeAnyLaterWork()
    {
        var connectionString = await CreateDatabaseAsync();
        await Orchestrator(connectionString, new Contributor(PersistenceModuleKey.Club,
            Script(PersistenceModuleKey.Club, 1, "0001", "CREATE TABLE club_one (id int);")))
            .MigrateAsync(TestContext.Current.CancellationToken);

        var changed = new Contributor(PersistenceModuleKey.Club,
            Script(PersistenceModuleKey.Club, 1, "0001", "CREATE TABLE club_one (id bigint);"),
            Script(PersistenceModuleKey.Club, 2, "0002", "CREATE TABLE club_two (id int);"));
        var ex = await Should.ThrowAsync<MigrationFailedException>(() =>
            Orchestrator(connectionString, changed).MigrateAsync(TestContext.Current.CancellationToken));

        ex.Message.ShouldContain("club/0001");
        (await ScalarsAsync(connectionString, "SELECT identity FROM socalytics_migrations.history")).ShouldBe(["0001"]);
        (await ScalarsAsync(connectionString,
            "SELECT count(*) FROM information_schema.tables WHERE table_name = 'club_two'")).ShouldBe(["0"]);
    }

    [Fact]
    public async Task FailedScriptRollsBackAndKeepsEarlierCommittedScripts()
    {
        var connectionString = await CreateDatabaseAsync();
        var contributor = new Contributor(PersistenceModuleKey.Club,
            Script(PersistenceModuleKey.Club, 1, "0001", "CREATE TABLE club_one (id int);"),
            Script(PersistenceModuleKey.Club, 2, "0002",
                "CREATE TABLE club_partial (id int); INSERT INTO missing_secret_table VALUES (1);"),
            Script(PersistenceModuleKey.Club, 3, "0003", "CREATE TABLE club_three (id int);"));

        var ex = await Should.ThrowAsync<MigrationFailedException>(() =>
            Orchestrator(connectionString, contributor).MigrateAsync(TestContext.Current.CancellationToken));

        ex.Message.ShouldContain("club/0002");
        ex.Message.ShouldNotContain("missing_secret_table");
        ex.Message.ShouldNotContain(connectionString);
        (await ScalarsAsync(connectionString, "SELECT identity FROM socalytics_migrations.history")).ShouldBe(["0001"]);
        (await ScalarsAsync(connectionString,
            "SELECT table_name FROM information_schema.tables WHERE table_name LIKE 'club_%' ORDER BY 1"))
            .ShouldBe(["club_one"]);
    }
}
