using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Infrastructure;
using SocAlytics.Platform.Infrastructure.Persistence;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Migrator;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Analysis;

public sealed class OutboxPublicationTests(PostgresContainerFixture postgres)
{
    private const string OutboxIdentity = "0010_analysis_outbox_messages";

    private static OutboxMessageDraft Draft(Guid id, Guid causation) =>
        new(id, OutboxMessageTypes.AnalysisReadyWork, "1.0.0", "analysis.ready-work",
            JsonDocument.Parse("{\"a\":1}").RootElement.Clone(), causation);

    private static ServiceProvider Provider(IsolatedDatabase db)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"ConnectionStrings:{DatabaseConnectionNames.Runtime}"] = db.AppConnectionString,
            })
            .Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddInfrastructure();
        return services.BuildServiceProvider();
    }

    private static async Task<long> CountAsync(IsolatedDatabase db, string where, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(db.MigratorConnectionString);
        await connection.OpenAsync(ct);
        return await connection.ExecuteScalarAsync<long>(
            new CommandDefinition($"SELECT count(*) FROM socalytics.outbox_messages WHERE {where}", cancellationToken: ct));
    }

    private async Task<IsolatedDatabase> MigratedAsync(CancellationToken ct)
    {
        var db = await postgres.CreateDatabaseAsync(ct);
        (await MigratorHarness.RunAsync(db, TestMigrationCatalogs.Platform(), ct)).ExitCode.ShouldBe(MigratorExitCode.Success);
        return db;
    }

    [Fact]
    public async Task Committed_scope_persists_one_pending_row()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await MigratedAsync(ct);
        await using var provider = Provider(db);
        await using var scope = provider.CreateAsyncScope();
        var id = Guid.NewGuid();
        await using (var uow = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().BeginAsync(ct))
        {
            (await scope.ServiceProvider.GetRequiredService<IOutbox>().EnqueueAsync(Draft(id, Guid.NewGuid()), ct)).ShouldBeTrue();
            await uow.CommitAsync(ct);
        }

        (await CountAsync(db, $"id = '{id}' AND state = 'pending' AND attempt_count = 0 AND publication_round = 1", ct)).ShouldBe(1);
    }

    [Fact]
    public async Task Rolled_back_or_disposed_scope_leaves_no_row()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await MigratedAsync(ct);
        await using var provider = Provider(db);
        await using (var scope = provider.CreateAsyncScope())
        {
            await using var uow = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().BeginAsync(ct);
            await scope.ServiceProvider.GetRequiredService<IOutbox>().EnqueueAsync(Draft(Guid.NewGuid(), Guid.NewGuid()), ct);
            await uow.RollbackAsync(ct);
        }

        await using (var scope = provider.CreateAsyncScope())
        {
            await using var uow = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().BeginAsync(ct);
            await scope.ServiceProvider.GetRequiredService<IOutbox>().EnqueueAsync(Draft(Guid.NewGuid(), Guid.NewGuid()), ct);
        }

        (await CountAsync(db, "true", ct)).ShouldBe(0);
    }

    [Fact]
    public async Task Same_type_and_causation_converges_on_first_id()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await MigratedAsync(ct);
        await using var provider = Provider(db);
        await using var scope = provider.CreateAsyncScope();
        var causation = Guid.NewGuid();
        var first = Guid.NewGuid();
        await using (var uow = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().BeginAsync(ct))
        {
            var outbox = scope.ServiceProvider.GetRequiredService<IOutbox>();
            (await outbox.EnqueueAsync(Draft(first, causation), ct)).ShouldBeTrue();
            (await outbox.EnqueueAsync(Draft(Guid.NewGuid(), causation), ct)).ShouldBeFalse();
            await uow.CommitAsync(ct);
        }

        (await CountAsync(db, $"causation_id = '{causation}'", ct)).ShouldBe(1);
        (await CountAsync(db, $"id = '{first}'", ct)).ShouldBe(1);
    }

    [Fact]
    public async Task Enqueue_outside_scope_throws()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await MigratedAsync(ct);
        await using var provider = Provider(db);
        await using var scope = provider.CreateAsyncScope();
        await Should.ThrowAsync<InvalidOperationException>(() =>
            scope.ServiceProvider.GetRequiredService<IOutbox>().EnqueueAsync(Draft(Guid.NewGuid(), Guid.NewGuid()), ct));
    }

    [Fact]
    public async Task Backfill_creates_one_pending_row_per_finalized_event()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await postgres.CreateDatabaseAsync(ct);
        (await MigratorHarness.RunAsync(db, TestMigrationCatalogs.Before(OutboxIdentity), ct)).ExitCode.ShouldBe(MigratorExitCode.Success);

        var account = Guid.NewGuid();
        var season = Guid.NewGuid();
        var team = Guid.NewGuid();
        var match = Guid.NewGuid();
        var set = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        await using (var connection = new NpgsqlConnection(db.MigratorConnectionString))
        {
            await connection.OpenAsync(ct);
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO socalytics.member_account (id, account_name, normalized_account_name, security_stamp, membership_changed_at, created_at)
                VALUES (@account, 'a', 'A', 's', now(), now());
                INSERT INTO socalytics.season (id, name, created_at) VALUES (@season, 'S', now());
                INSERT INTO socalytics.team (id, season_id, name, created_at) VALUES (@team, @season, 'T', now());
                INSERT INTO socalytics.match (id, team_id, opponent_name, kickoff_at, home_away, created_at, created_by_account_id)
                VALUES (@match, @team, 'O', now(), 'home', now(), @account);
                INSERT INTO socalytics.recording_set_versions (id, match_id, team_id, member_count, created_by, finalized_at)
                VALUES (@set, @match, @team, 1, @account, now());
                INSERT INTO socalytics.recording_finalized_events (event_id, event_type, contract_version, recording_set_version_id, match_id, team_id, occurred_at, payload)
                VALUES (@eventId, 'matches.recordings-finalized', '1.0.0', @set, @match, @team, now(), '{"x":1}'::jsonb);
                """,
                new { account, season, team, match, set, eventId },
                cancellationToken: ct));
        }

        (await MigratorHarness.RunAsync(db, TestMigrationCatalogs.Platform(), ct)).ExitCode.ShouldBe(MigratorExitCode.Success);
        (await CountAsync(db, $"id = '{eventId}' AND causation_id = '{eventId}' AND state = 'pending' AND message_type = 'matches.recordings-finalized'", ct)).ShouldBe(1);
        (await CountAsync(db, "true", ct)).ShouldBe(1);

        (await MigratorHarness.RunAsync(db, TestMigrationCatalogs.Platform(), ct)).ExitCode.ShouldBe(MigratorExitCode.Success);
        (await CountAsync(db, "true", ct)).ShouldBe(1);
    }
}
