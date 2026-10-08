using Npgsql;
using Shouldly;
using SocAlytics.Platform.Infrastructure.Persistence.Migrations;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Migrator;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Concurrency;

public sealed class MigrationVersionAdvancementTests(PostgresContainerFixture postgres)
{
    private static MigrationCatalog CatalogUpTo(int lastDataSequence)
    {
        var catalog = TestMigrationCatalogs.With("Versioning", "VersioningData");
        return MigrationCatalog.Create(catalog.Scripts.Where(script =>
            script.Area != "test" || script.Sequence < 9002 || script.Sequence <= lastDataSequence));
    }

    private static async Task ExecAsync(NpgsqlConnection connection, string sql, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<Dictionary<Guid, long>> VersionsAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        var result = new Dictionary<Guid, long>();
        await using var command = new NpgsqlCommand("SELECT id, version FROM socalytics.test_widget", connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result[reader.GetGuid(0)] = reader.GetInt64(1);
        }

        return result;
    }

    [Fact]
    public async Task DataMigrationAdvancesAndSuppressedMigrationDoesNot()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await postgres.CreateDatabaseAsync(ct);

        (await MigratorHarness.RunAsync(db, TestMigrationCatalogs.With("Versioning"), ct)).ExitCode
            .ShouldBe(MigratorExitCode.Success);

        await using var app = new NpgsqlConnection(db.AppConnectionString);
        await app.OpenAsync(ct);

        var widgets = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        foreach (var id in widgets)
        {
            await ExecAsync(app, $"INSERT INTO socalytics.test_widget (id, name) VALUES ('{id}', 'w')", ct);
        }

        var partOwner = widgets[1];
        var partId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        await ExecAsync(app, $"INSERT INTO socalytics.test_widget_part (id, widget_id, label) VALUES ('{partId}', '{partOwner}', 'p')", ct);
        var seeded = await VersionsAsync(app, ct);

        (await MigratorHarness.RunAsync(db, () => CatalogUpTo(9002), ct)).ExitCode
            .ShouldBe(MigratorExitCode.Success);

        var afterBackfill = await VersionsAsync(app, ct);
        foreach (var id in widgets)
        {
            afterBackfill[id].ShouldBe(seeded[id] + (id == partOwner ? 2 : 1));
        }

        (await MigratorHarness.RunAsync(db, () => CatalogUpTo(9003), ct)).ExitCode
            .ShouldBe(MigratorExitCode.Success);

        (await VersionsAsync(app, ct)).ShouldBe(afterBackfill);

        var first = widgets[0];
        await ExecAsync(app, $"UPDATE socalytics.test_widget SET name = 'later' WHERE id = '{first}'", ct);
        (await VersionsAsync(app, ct))[first].ShouldBe(afterBackfill[first] + 1);
    }
}
