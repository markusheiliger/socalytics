using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Shouldly;
using SocAlytics.Platform.Infrastructure.Persistence.Migrations;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Migrator;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Migrations;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class MigratorExitCodeCollection
{
    public const string Name = "Migrator exit codes";
}

[Collection(MigratorExitCodeCollection.Name)]
public sealed class MigratorExitCodeTests(PostgresContainerFixture postgres)
{
    private const string ConnectionStringKey = "--ConnectionStrings:socalytics-migrator=";

    private static async Task<long> CountAsync(IsolatedDatabase db, string sql, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(db.MigratorConnectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        return (long)(await command.ExecuteScalarAsync(ct))!;
    }

    private static async Task WaitForSlowScriptAsync(IsolatedDatabase db, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (await CountAsync(
            db,
            "SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND query LIKE '%pg_sleep%' AND query NOT LIKE '%pg_stat_activity%'",
            ct) == 0)
        {
            DateTime.UtcNow.ShouldBeLessThan(deadline, "the slow script never started");
            await Task.Delay(50, ct);
        }
    }

    private static void AssertFailure(MigratorRunResult result, string category) =>
        result.Logs.Single(e => e.EventId == 1100).State["Category"].ShouldBe(category);

    private static async Task AssertSlowRolledBackAsync(IsolatedDatabase db, CancellationToken ct)
    {
        (await CountAsync(db, "SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname = 'socalytics' AND c.relname = 'test_slow'", ct))
            .ShouldBe(0);
        (await CountAsync(db, "SELECT count(*) FROM socalytics_migrations.history WHERE identity = '9001_test_slow'", ct))
            .ShouldBe(0);
    }

    [Fact]
    public void MigratorProgramMainIsTheAssemblyEntryPoint()
    {
        var entryPoint = typeof(MigratorEntryPoint).Assembly.EntryPoint;

        entryPoint.ShouldNotBeNull();
        entryPoint.DeclaringType!.FullName.ShouldBe("SocAlytics.Platform.Migrator.Program");
        // The compiler synthesizes <Main> that calls the async Main(string[]).
        entryPoint.DeclaringType.GetMethod(
            "Main",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            .ShouldNotBeNull();
    }

    [Fact]
    public async Task CurrentDatabaseExitsWithSuccess()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await postgres.CreateDatabaseAsync(ct);

        (await MigratorHarness.RunAsync(db, MigrationCatalog.Platform, ct)).ExitCode.ShouldBe(MigratorExitCode.Success);
        (await MigratorHarness.RunAsync(db, MigrationCatalog.Platform, ct)).ExitCode.ShouldBe(MigratorExitCode.Success);
    }

    [Fact]
    public async Task MissingConnectionStringExitsWithConfigurationInvalid()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await postgres.CreateDatabaseAsync(ct);

        var result = await MigratorHarness.RunAsync(db, MigrationCatalog.Platform, ct, null, ConnectionStringKey);

        result.ExitCode.ShouldBe(MigratorExitCode.ConfigurationInvalid);
        AssertFailure(result, "configuration");
    }

    [Fact]
    public async Task NonPositiveScriptTimeoutExitsWithConfigurationInvalid()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await postgres.CreateDatabaseAsync(ct);

        var result = await MigratorHarness.RunAsync(
            db, MigrationCatalog.Platform, ct, null, "--Migrator:ScriptTimeout=00:00:00");

        result.ExitCode.ShouldBe(MigratorExitCode.ConfigurationInvalid);
        AssertFailure(result, "configuration");
    }

    [Fact]
    public async Task UnreachableHostExitsWithDatabaseUnavailable()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await postgres.CreateDatabaseAsync(ct);
        var unreachable = new NpgsqlConnectionStringBuilder(db.MigratorConnectionString)
        {
            Host = "127.0.0.1",
            Port = 1,
        }.ConnectionString;

        var result = await MigratorHarness.RunAsync(
            db, MigrationCatalog.Platform, ct, null, ConnectionStringKey + unreachable, "--Migrator:ConnectTimeout=00:00:03");

        result.ExitCode.ShouldBe(MigratorExitCode.DatabaseUnavailable);
        AssertFailure(result, "database-unavailable");
    }

    [Fact]
    public async Task WrongPasswordExitsWithDatabaseUnavailable()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await postgres.CreateDatabaseAsync(ct);
        var wrongPassword = new NpgsqlConnectionStringBuilder(db.MigratorConnectionString)
        {
            Password = "definitely-not-the-password",
        }.ConnectionString;

        var result = await MigratorHarness.RunAsync(
            db, MigrationCatalog.Platform, ct, null, ConnectionStringKey + wrongPassword, "--Migrator:ConnectTimeout=00:00:03");

        result.ExitCode.ShouldBe(MigratorExitCode.DatabaseUnavailable);
        AssertFailure(result, "database-unavailable");
    }

    [Fact]
    public async Task DatabaseThatBecomesReachableAfterTheRunStartedExitsWithSuccess()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var dedicated = await postgres.CreateDedicatedContainerAsync(ct);
        await using var scratch = await postgres.CreateDatabaseAsync(ct);

        await dedicated.PauseAsync(ct);
        var paused = true;
        try
        {
            var run = Task.Run(() => MigratorHarness.RunAsync(
                scratch,
                MigrationCatalog.Platform,
                ct,
                null,
                ConnectionStringKey + dedicated.MigratorConnectionString,
                "--Migrator:ConnectTimeout=00:01:00"), ct);

            await Task.Delay(TimeSpan.FromSeconds(3), ct);
            await dedicated.UnpauseAsync(ct);
            paused = false;

            var result = await run;
            result.ExitCode.ShouldBe(MigratorExitCode.Success);
        }
        finally
        {
            if (paused)
            {
                await dedicated.UnpauseAsync(CancellationToken.None);
            }
        }

        await using var connection = new NpgsqlConnection(dedicated.MigratorConnectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand("SELECT count(*) FROM socalytics_migrations.history", connection);
        ((long)(await command.ExecuteScalarAsync(ct))!).ShouldBe(MigrationCatalog.Platform.Scripts.Count);
    }

    [Fact]
    public async Task DuplicateSequenceInDeferredCatalogExitsWithCatalogInvalid()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await postgres.CreateDatabaseAsync(ct);

        var result = await MigratorHarness.RunAsync(
            db,
            () => MigrationCatalog.Create(
            [
                TestMigrationCatalogs.Script("9001_test_a", "SELECT 1;"),
                TestMigrationCatalogs.Script("9001_test_b", "SELECT 1;"),
            ]),
            ct);

        result.ExitCode.ShouldBe(MigratorExitCode.CatalogInvalid);
        AssertFailure(result, "catalog-invalid");
    }

    [Fact]
    public async Task MalformedScriptNameInDeferredCatalogExitsWithCatalogInvalid()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await postgres.CreateDatabaseAsync(ct);

        var result = await MigratorHarness.RunAsync(
            db,
            () => MigrationCatalog.Create([TestMigrationCatalogs.Script("Not-A-Valid-Name", "SELECT 1;")]),
            ct);

        result.ExitCode.ShouldBe(MigratorExitCode.CatalogInvalid);
        AssertFailure(result, "catalog-invalid");
    }

    [Fact]
    public async Task CancellationWhileScriptRunsExitsWithCancelledAndRollsBack()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await postgres.CreateDatabaseAsync(ct);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var run = Task.Run(() => MigratorHarness.RunAsync(db, TestMigrationCatalogs.With("Slow"), cts.Token), ct);
        await WaitForSlowScriptAsync(db, ct);
        await Task.Delay(TimeSpan.FromSeconds(1), ct);
        await cts.CancelAsync();

        var result = await run;
        result.ExitCode.ShouldBe(MigratorExitCode.Cancelled);
        AssertFailure(result, "cancelled");
        await AssertSlowRolledBackAsync(db, ct);
    }

    [Fact]
    public async Task HostShutdownWhileScriptRunsExitsWithCancelledAndRollsBack()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await postgres.CreateDatabaseAsync(ct);

        var result = await MigratorHarness.RunAsync(
            db,
            TestMigrationCatalogs.With("Slow"),
            ct,
            host =>
            {
                var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
                _ = Task.Run(async () =>
                {
                    await WaitForSlowScriptAsync(db, ct);
                    await Task.Delay(TimeSpan.FromSeconds(1), ct);
                    lifetime.StopApplication();
                }, ct);
            });

        result.ExitCode.ShouldBe(MigratorExitCode.Cancelled);
        AssertFailure(result, "cancelled");
        await AssertSlowRolledBackAsync(db, ct);
    }
}
