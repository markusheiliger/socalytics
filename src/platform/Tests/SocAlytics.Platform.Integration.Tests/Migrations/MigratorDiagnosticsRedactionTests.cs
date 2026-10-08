using Npgsql;
using Shouldly;
using SocAlytics.Platform.Infrastructure.Persistence.Migrations;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Migrator;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Migrations;

// Console redirection is process-wide, so these tests must not run in parallel with others.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class MigratorDiagnosticsRedactionCollection
{
    public const string Name = "Migrator diagnostics redaction";
}

[Collection(MigratorDiagnosticsRedactionCollection.Name)]
public sealed class MigratorDiagnosticsRedactionTests(PostgresContainerFixture postgres)
{
    private const string Marker = "marker_7f3c9a1e";
    private const string ConnectionStringKey = "--ConnectionStrings:socalytics-migrator=";

    private static async Task<(MigratorRunResult Result, string Console)> RunCapturingConsoleAsync(
        IsolatedDatabase db,
        MigrationCatalog catalog,
        CancellationToken ct,
        params string[] extraArguments)
    {
        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var captured = new StringWriter();
        var synchronized = TextWriter.Synchronized(captured);
        Console.SetOut(synchronized);
        Console.SetError(synchronized);
        try
        {
            var result = await MigratorHarness.RunAsync(db, catalog, ct, null, extraArguments);
            return (result, captured.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

    private static string AllOutput(MigratorRunResult result, string console) =>
        string.Join(
            "\n",
            result.Logs.SelectMany(e => new[] { e.Message, e.Exception ?? string.Empty }.Concat(e.State.Values.Select(v => v ?? string.Empty)))
                .Append(console));

    [Fact]
    public async Task FailingMigrationContentIsNotDisclosed()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await postgres.CreateDatabaseAsync(ct);

        var (result, console) = await RunCapturingConsoleAsync(db, TestMigrationCatalogs.With("Marker"), ct);

        result.ExitCode.ShouldBe(MigratorExitCode.MigrationFailed);
        var failure = result.Logs.Single(e => e.EventId == 1100);
        failure.State["Identity"].ShouldBe("9001_test_content_marker");
        failure.State["SqlState"].ShouldBe("42601");

        var output = AllOutput(result, console);
        output.ShouldNotContain(Marker);
        output.ShouldNotContain(postgres.MigratorPassword);
    }

    [Fact]
    public async Task UnreachableHostDoesNotDisclosePasswordOrConnectionString()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await postgres.CreateDatabaseAsync(ct);
        var unreachable = new NpgsqlConnectionStringBuilder(db.MigratorConnectionString)
        {
            Host = "127.0.0.1",
            Port = 1,
        }.ConnectionString;

        var (result, console) = await RunCapturingConsoleAsync(
            db, MigrationCatalog.Platform, ct, ConnectionStringKey + unreachable, "--Migrator:ConnectTimeout=00:00:03");

        result.ExitCode.ShouldBe(MigratorExitCode.DatabaseUnavailable);
        var output = AllOutput(result, console);
        output.ShouldNotContain(postgres.MigratorPassword);
        output.ShouldNotContain(unreachable);
    }

    [Fact]
    public async Task WrongPasswordDoesNotDisclosePasswordOrConnectionString()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await postgres.CreateDatabaseAsync(ct);
        const string wrongPassword = "wrong_pw_5d2e9b41";
        var wrong = new NpgsqlConnectionStringBuilder(db.MigratorConnectionString)
        {
            Password = wrongPassword,
        }.ConnectionString;

        var (result, console) = await RunCapturingConsoleAsync(
            db, MigrationCatalog.Platform, ct, ConnectionStringKey + wrong);

        result.ExitCode.ShouldNotBe(MigratorExitCode.Success);
        var output = AllOutput(result, console);
        output.ShouldNotContain(wrongPassword);
        output.ShouldNotContain(postgres.MigratorPassword);
        output.ShouldNotContain(wrong);
    }
}
