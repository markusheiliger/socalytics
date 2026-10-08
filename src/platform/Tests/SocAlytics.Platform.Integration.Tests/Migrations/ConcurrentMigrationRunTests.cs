using Npgsql;
using Shouldly;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Migrator;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Migrations;

public sealed class ConcurrentMigrationRunTests(PostgresContainerFixture postgres)
{
    private const string AdvisoryLockGranted = """
        SELECT count(*) FROM pg_locks
        WHERE locktype = 'advisory' AND granted
          AND database = (SELECT oid FROM pg_database WHERE datname = current_database())
        """;

    private static async Task<long> CountAsync(IsolatedDatabase db, string sql, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(db.MigratorConnectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        return (long)(await command.ExecuteScalarAsync(ct))!;
    }

    private static async Task WaitForLockAsync(IsolatedDatabase db, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (await CountAsync(db, AdvisoryLockGranted, ct) == 0)
        {
            DateTime.UtcNow.ShouldBeLessThan(deadline, "the first run never acquired the migration lock");
            await Task.Delay(50, ct);
        }
    }

    [Fact]
    public async Task ConcurrentRunsApplyEachScriptOnceAndTheWaiterLogsOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await postgres.CreateDatabaseAsync(ct);
        var catalog = TestMigrationCatalogs.With("Slow");

        var first = Task.Run(() => MigratorHarness.RunAsync(
            db, catalog, ct, null, "--Migrator:LockWaitTimeout=00:02:00"), ct);
        await WaitForLockAsync(db, ct);
        var second = await MigratorHarness.RunAsync(
            db, catalog, ct, null, "--Migrator:LockWaitTimeout=00:02:00");
        var firstResult = await first;

        firstResult.ExitCode.ShouldBe(MigratorExitCode.Success);
        second.ExitCode.ShouldBe(MigratorExitCode.Success);
        second.Logs.Count(e => e.EventId == 1001).ShouldBe(1);
        second.Logs.ShouldNotContain(e => e.EventId == 1004 || e.EventId == 1005);
        firstResult.Logs.ShouldNotContain(e => e.EventId == 1001);

        (await CountAsync(db, "SELECT count(*) FROM socalytics_migrations.history", ct))
            .ShouldBe(catalog.Scripts.Count);
        (await CountAsync(db, "SELECT count(DISTINCT identity) FROM socalytics_migrations.history", ct))
            .ShouldBe(catalog.Scripts.Count);
    }

    [Fact]
    public async Task LockWaitTimeoutExitsWithLockTimeoutAndChangesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await postgres.CreateDatabaseAsync(ct);
        var catalog = TestMigrationCatalogs.With("Slow");

        var first = Task.Run(() => MigratorHarness.RunAsync(db, catalog, ct), ct);
        await WaitForLockAsync(db, ct);
        var second = await MigratorHarness.RunAsync(
            db, catalog, ct, null, "--Migrator:LockWaitTimeout=00:00:01");

        second.ExitCode.ShouldBe(MigratorExitCode.LockTimeout);
        var failure = second.Logs.Single(e => e.EventId == 1100);
        failure.State["Category"].ShouldBe("lock-timeout");
        second.Logs.ShouldNotContain(e => e.EventId == 1004 || e.EventId == 1005);
        (await CountAsync(db, "SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname = 'socalytics' AND c.relname = 'test_slow'", ct))
            .ShouldBe(0);

        (await first).ExitCode.ShouldBe(MigratorExitCode.Success);
        (await CountAsync(db, "SELECT count(*) FROM socalytics_migrations.history", ct))
            .ShouldBe(catalog.Scripts.Count);
    }

    [Fact]
    public async Task RunsAgainstDifferentDatabasesDoNotWaitForEachOther()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var dbA = await postgres.CreateDatabaseAsync(ct);
        await using var dbB = await postgres.CreateDatabaseAsync(ct);
        var catalog = TestMigrationCatalogs.With("Slow");

        var a = Task.Run(() => MigratorHarness.RunAsync(dbA, catalog, ct), ct);
        await WaitForLockAsync(dbA, ct);
        var b = await MigratorHarness.RunAsync(dbB, catalog, ct, null, "--Migrator:LockWaitTimeout=00:00:01");

        b.ExitCode.ShouldBe(MigratorExitCode.Success);
        b.Logs.ShouldNotContain(e => e.EventId == 1001);
        (await a).ExitCode.ShouldBe(MigratorExitCode.Success);
    }
}
