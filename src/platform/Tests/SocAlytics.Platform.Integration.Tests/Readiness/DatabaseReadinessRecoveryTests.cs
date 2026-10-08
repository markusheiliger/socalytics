using System.Diagnostics;
using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Shouldly;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Migrator;
using Xunit;
using static SocAlytics.Platform.Integration.Tests.Readiness.DatabaseReadinessTests;

namespace SocAlytics.Platform.Integration.Tests.Readiness;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class DatabaseReadinessRecoveryCollection
{
    public const string Name = "Database readiness recovery";
}

[Collection(DatabaseReadinessRecoveryCollection.Name)]
public sealed class DatabaseReadinessRecoveryTests(PostgresContainerFixture postgres)
{
    private const string ValidChecksum = "sha-256:0000000000000000000000000000000000000000000000000000000000000000";

    private static async Task ExecuteAsync(string connectionString, string database, string sql, CancellationToken ct)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString) { Database = database, Pooling = false };
        await using var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<IsolatedDatabase> MigratedAsync(PostgresContainerFixture fixture, CancellationToken ct)
    {
        var db = await fixture.CreateDatabaseAsync(ct);
        (await MigratorHarness.RunAsync(db, TestMigrationCatalogs.Platform(), ct)).ExitCode.ShouldBe(MigratorExitCode.Success);
        return db;
    }

    private static void AssertNoSecrets(CapturingLoggerProvider capture, params string[] secrets)
    {
        foreach (var entry in capture.Entries)
        {
            var text = string.Join('\n', entry.Message, entry.Exception, string.Join('\n', entry.State.Values));
            foreach (var secret in secrets)
            {
                text.ShouldNotContain(secret);
            }
        }
    }

    [Fact]
    public async Task ChecksumMismatchReportsConflictWithIdentity()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await MigratedAsync(postgres, ct);
        var identity = MigrationCatalogIdentity();
        await ExecuteAsync(db.SuperuserConnectionString, db.Name,
            $"UPDATE socalytics_migrations.history SET checksum = '{ValidChecksum}' WHERE identity = '{identity}'", ct);

        var capture = new CapturingLoggerProvider();
        await using var factory = CreateFactory(db.AppConnectionString, capture);
        using var client = factory.CreateClient();

        (await client.GetAsync("/health", ct)).StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await DescriptionAsync(factory, ct)).ShouldBe($"migration-state-conflict {identity}");
        AssertNoSecrets(capture, postgres.AppPassword, db.AppConnectionString);
    }

    [Fact]
    public async Task UnknownHigherSequenceRowWithPendingPlatformScriptIsConflict()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await MigratedAsync(postgres, ct);
        await ExecuteAsync(db.SuperuserConnectionString, db.Name,
            $"DELETE FROM socalytics_migrations.history; INSERT INTO socalytics_migrations.history (sequence, identity, checksum) VALUES (9999, 'unknown_conflict_row', '{ValidChecksum}')",
            ct);

        await using var factory = CreateFactory(db.AppConnectionString, new CapturingLoggerProvider());
        using var client = factory.CreateClient();

        (await client.GetAsync("/health", ct)).StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await DescriptionAsync(factory, ct)).ShouldStartWith("migration-state-conflict");
    }

    [Fact]
    public async Task UnknownAppliedRowsKeepHealthyAndWarnOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await MigratedAsync(postgres, ct);
        await ExecuteAsync(db.SuperuserConnectionString, db.Name,
            $"INSERT INTO socalytics_migrations.history (sequence, identity, checksum) VALUES (9998, 'unknown_readiness_row', '{ValidChecksum}')",
            ct);

        var capture = new CapturingLoggerProvider();
        await using var factory = CreateFactory(db.AppConnectionString, capture);
        using var client = factory.CreateClient();

        await ShouldBecomeHealthyAsync(client, ct);
        for (var i = 0; i < 3; i++)
        {
            (await client.GetAsync("/health", ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        var warnings = capture.Entries.Where(e => e.EventId == 2000).ToList();
        warnings.Count.ShouldBe(1);
        warnings[0].Message.ShouldContain("unknown_readiness_row");
    }

    [Fact]
    public async Task UnreachableDatabaseReportsUnavailableWithinTimeoutWhileAliveStaysUp()
    {
        var ct = TestContext.Current.CancellationToken;
        var capture = new CapturingLoggerProvider();
        const string connectionString = "Host=127.0.0.1;Port=1;Username=socalytics_app;Password=unreachable-secret;Database=socalytics";
        await using var factory = CreateFactory(connectionString, capture);
        using var client = factory.CreateClient();

        var stopwatch = Stopwatch.StartNew();
        (await client.GetAsync("/health", ct)).StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5));
        (await DescriptionAsync(factory, ct)).ShouldBe("database-unavailable");
        (await client.GetAsync("/alive", ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        AssertNoSecrets(capture, "unreachable-secret", connectionString);
    }

    [Fact]
    public async Task RevokedHistoryAccessReportsAccessDenied()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await MigratedAsync(postgres, ct);
        await ExecuteAsync(db.SuperuserConnectionString, db.Name,
            "REVOKE SELECT ON socalytics_migrations.history FROM socalytics_app", ct);

        var capture = new CapturingLoggerProvider();
        await using var factory = CreateFactory(db.AppConnectionString, capture);
        using var client = factory.CreateClient();

        (await client.GetAsync("/health", ct)).StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await DescriptionAsync(factory, ct)).ShouldStartWith("database-access-denied");
        AssertNoSecrets(capture, postgres.AppPassword, db.AppConnectionString);
    }

    [Fact]
    public async Task ReadinessRecoversAfterDatabasePauseWithoutRestart()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = await postgres.CreateDedicatedContainerAsync(ct);
        await using var db = await IsolatedDatabase.CreateAsync(server, ct);
        (await MigratorHarness.RunAsync(db, TestMigrationCatalogs.Platform(), ct)).ExitCode.ShouldBe(MigratorExitCode.Success);

        var capture = new CapturingLoggerProvider();
        await using var factory = CreateFactory(db.AppConnectionString, capture);
        using var client = factory.CreateClient();
        await ShouldBecomeHealthyAsync(client, ct);

        await server.PauseAsync(ct);
        try
        {
            var stopwatch = Stopwatch.StartNew();
            (await client.GetAsync("/health", ct)).StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
            stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(10));
            (await DescriptionAsync(factory, ct)).ShouldBe("database-unavailable");
        }
        finally
        {
            await server.UnpauseAsync(ct);
        }

        var deadline = DateTime.UtcNow.AddSeconds(30);
        HttpStatusCode status;
        do
        {
            status = (await client.GetAsync("/health", ct)).StatusCode;
            if (status != HttpStatusCode.OK)
            {
                await Task.Delay(500, ct);
            }
        }
        while (status != HttpStatusCode.OK && DateTime.UtcNow < deadline);

        status.ShouldBe(HttpStatusCode.OK);
        AssertNoSecrets(capture, server.AppPassword, db.AppConnectionString);
    }

    private static string MigrationCatalogIdentity() =>
        SocAlytics.Platform.Infrastructure.Persistence.Migrations.MigrationCatalog.Platform.Scripts[0].Identity;
}
