using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using SocAlytics.Platform.Persistence;
using Testcontainers.PostgreSql;
using Xunit;

namespace SocAlytics.Platform.Persistence.Tests;

public sealed class MigrationOrchestrationTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _database = new PostgreSqlBuilder("postgres:17-alpine").Build();

    public async ValueTask InitializeAsync() => await _database.StartAsync(TestContext.Current.CancellationToken);

    public async ValueTask DisposeAsync() => await _database.DisposeAsync();

    [Fact]
    public async Task Clean_migrations_follow_module_and_numeric_sequence_order_and_repeat_without_changes()
    {
        var initial = Migration(PersistenceModuleIdentity.Club, 1, "z-initial'quoted", "initial");
        var append = Migration(PersistenceModuleIdentity.Club, 10, "a-append", "append");
        var middle = Migration(PersistenceModuleIdentity.Club, 2, "middle");
        var others = PersistenceModuleIdentity.All.Skip(1)
            .Select(module => Migration(module, 1, "initial")).ToArray();
        await using var services = Services([.. others.Reverse(), append, middle, initial]);

        await services.MigratePlatformDatabaseAsync(TestContext.Current.CancellationToken);

        var history = await HistoryAsync();
        history.Select(row => (row.Module, row.Sequence, row.Identity, row.Checksum)).ShouldBe(
            new[] { initial, middle, append }.Concat(others)
                .Select(migration => (migration.Module.Key, migration.Sequence, migration.Identity, migration.Checksum)));
        history.ShouldAllBe(row => row.AppliedAt != default);
        (await ValuesAsync()).ShouldBe(["initial", "later"]);
        var schema = await SchemaAsync();

        await services.MigratePlatformDatabaseAsync(TestContext.Current.CancellationToken);

        (await HistoryAsync()).ShouldBe(history);
        (await ValuesAsync()).ShouldBe(["initial", "later"]);
        (await SchemaAsync()).ShouldBe(schema);
    }

    [Theory]
    [InlineData("checksum")]
    [InlineData("sequence")]
    [InlineData("identity")]
    public async Task Full_history_preflight_rejects_conflicts_before_any_pending_work(string conflict)
    {
        var initial = Migration(PersistenceModuleIdentity.Club, 1, "initial", "initial");
        var applied = Migration(PersistenceModuleIdentity.Analysis, 1, "analysis-initial");
        await using (var services = Services([applied, initial]))
        {
            await services.MigratePlatformDatabaseAsync(TestContext.Current.CancellationToken);
        }

        var history = await HistoryAsync();
        var pending = Migration(PersistenceModuleIdentity.Club, 2, "pending", "append");
        var changed = Migration(
            PersistenceModuleIdentity.Analysis,
            conflict == "sequence" ? 2 : 1,
            conflict == "identity" ? "renamed" : applied.Identity,
            conflict == "checksum" ? "append" : null);
        await using var conflictingServices = Services([pending, initial, changed]);

        var error = await Should.ThrowAsync<MigrationException>(
            () => conflictingServices.MigratePlatformDatabaseAsync(TestContext.Current.CancellationToken));

        error.Message.ShouldContain("analysis");
        error.Message.ShouldContain(changed.Identity);
        error.InnerException.ShouldBeNull();
        (await HistoryAsync()).ShouldBe(history);
        (await ValuesAsync()).ShouldBe(["initial"]);
    }

    [Fact]
    public async Task Failed_script_rolls_back_its_ddl_data_and_history_but_preserves_earlier_commits()
    {
        var initial = Migration(PersistenceModuleIdentity.Club, 1, "initial", "initial");
        var failed = Migration(PersistenceModuleIdentity.Club, 2, "failing-script", "failure");
        var later = Migration(PersistenceModuleIdentity.Club, 3, "later", "append");
        await using var services = Services([later, failed, initial]);

        var error = await Should.ThrowAsync<MigrationException>(
            () => services.MigratePlatformDatabaseAsync(TestContext.Current.CancellationToken));

        error.Message.ShouldContain("club");
        error.Message.ShouldContain(failed.Identity);
        error.ToString().ShouldNotContain("provider-detail-must-not-leak");
        error.ToString().ShouldNotContain(_database.GetConnectionString());
        error.InnerException.ShouldBeNull();
        (await HistoryAsync()).Select(row => row.Identity).ShouldBe([initial.Identity]);
        (await ValuesAsync()).ShouldBe(["initial"]);
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand("SELECT to_regclass('club.failed_probe') IS NULL", connection);
        (await command.ExecuteScalarAsync(TestContext.Current.CancellationToken)).ShouldBe(true);
    }

    [Fact]
    public async Task Failed_history_insert_rolls_back_the_successful_script_in_the_same_transaction()
    {
        var initial = Migration(PersistenceModuleIdentity.Club, 1, "initial", "initial");
        await using (var services = Services([initial]))
        {
            await services.MigratePlatformDatabaseAsync(TestContext.Current.CancellationToken);
        }

        var history = await HistoryAsync();
        await using (var connection = await OpenAsync())
        await using (var command = new NpgsqlCommand(
            """
            CREATE FUNCTION socalytics_migrations.reject_history() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                RAISE EXCEPTION 'provider-detail-must-not-leak';
            END
            $$;
            CREATE TRIGGER reject_history BEFORE INSERT ON socalytics_migrations.history
            FOR EACH ROW EXECUTE FUNCTION socalytics_migrations.reject_history();
            """,
            connection))
        {
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        await using var pendingServices = Services(
            [initial, Migration(PersistenceModuleIdentity.Club, 2, "append", "append")]);

        var error = await Should.ThrowAsync<MigrationException>(
            () => pendingServices.MigratePlatformDatabaseAsync(TestContext.Current.CancellationToken));

        error.ToString().ShouldNotContain("provider-detail-must-not-leak");
        (await HistoryAsync()).ShouldBe(history);
        (await ValuesAsync()).ShouldBe(["initial"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Database_preparation_failure_does_not_expose_connection_details(bool malformed)
    {
        var invalid = malformed
            ? "provider-detail-must-not-leak=invalid"
            : new NpgsqlConnectionStringBuilder(_database.GetConnectionString())
            {
                Database = "missing_database"
            }.ConnectionString;
        await using var services = Services([], invalid);

        var error = await Should.ThrowAsync<MigrationException>(
            () => services.MigratePlatformDatabaseAsync(TestContext.Current.CancellationToken));

        error.Message.ShouldBe("Migration preparation failed.");
        error.InnerException.ShouldBeNull();
        error.ToString().ShouldNotContain(invalid);
        error.ToString().ShouldNotContain("missing_database");
        error.ToString().ShouldNotContain("provider-detail-must-not-leak");
    }

    private ServiceProvider Services(IEnumerable<MigrationDescriptor> migrations, string? connectionString = null)
    {
        var services = new ServiceCollection();
        services.AddPlatformPersistence(
            _database.GetConnectionString(), connectionString ?? _database.GetConnectionString());
        foreach (var migration in migrations)
        {
            services.AddSingleton(migration);
        }

        return services.BuildServiceProvider();
    }

    private static MigrationDescriptor Migration(
        PersistenceModuleIdentity module,
        int sequence,
        string identity,
        string? fixture = null)
    {
        return MigrationDescriptor.FromEmbeddedResource(
            module,
            sequence,
            identity,
            typeof(MigrationOrchestrationTests).Assembly,
            fixture is null
                ? "SocAlytics.Platform.Persistence.Tests.sample.sql"
                : $"SocAlytics.Platform.Persistence.Tests.Fixtures.Migrations.{fixture}.sql");
    }

    private async Task<NpgsqlConnection> OpenAsync()
    {
        var connection = new NpgsqlConnection(_database.GetConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        return connection;
    }

    private async Task<List<HistoryRow>> HistoryAsync()
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT module_key, sequence, script_identity, checksum, applied_at
            FROM socalytics_migrations.history ORDER BY applied_at
            """,
            connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        var rows = new List<HistoryRow>();
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            rows.Add(new HistoryRow(
                reader.GetString(0), reader.GetInt32(1), reader.GetString(2),
                reader.GetString(3), reader.GetDateTime(4)));
        }

        return rows;
    }

    private async Task<string[]> ValuesAsync()
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT array_agg(value ORDER BY value) FROM club.migration_probe", connection);
        return (string[])(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    private async Task<string[]> SchemaAsync()
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT array_agg(table_schema || '.' || table_name || '.' || column_name || ':' || data_type
                ORDER BY table_schema, table_name, ordinal_position)
            FROM information_schema.columns WHERE table_schema IN ('club', 'socalytics_migrations')
            """,
            connection);
        return (string[])(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    private sealed record HistoryRow(string Module, int Sequence, string Identity, string Checksum, DateTime AppliedAt);
}
