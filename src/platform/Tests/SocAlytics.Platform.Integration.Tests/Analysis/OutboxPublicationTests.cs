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
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using SocAlytics.Platform.Application.Messaging;
using SocAlytics.Platform.Infrastructure.Messaging.Nats;
using SocAlytics.Platform.Integration.Tests.Analysis.Support;
using SocAlytics.Platform.Integration.Tests.Recordings;

namespace SocAlytics.Platform.Integration.Tests.Analysis;

[Collection("NatsFaultInjection")]
public sealed class OutboxPublicationTests(PostgresContainerFixture postgres, NatsContainerFixture nats, RustFsContainerFixture rustFs)
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

    private static async Task<Guid> InsertPendingAsync(IsolatedDatabase db, DateTimeOffset createdAt, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        await using var connection = new NpgsqlConnection(db.MigratorConnectionString);
        await connection.OpenAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO socalytics.outbox_messages (id, message_type, contract_version, subject, payload, causation_id, state, created_at)
            VALUES (@id, 'analysis.ready-work', '1.0.0', 'analysis.jobs.ready.test', '{"a":1}'::jsonb, @id, 'pending', @createdAt)
            """,
            new { id, createdAt }, cancellationToken: ct));
        return id;
    }

    private static async Task<(string State, int Attempts, DateTimeOffset? Next, DateTimeOffset? Published, string? Category)> RowAsync(
        IsolatedDatabase db, Guid id, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(db.MigratorConnectionString);
        await connection.OpenAsync(ct);
        var r = await connection.QuerySingleAsync(new CommandDefinition(
            "SELECT state, attempt_count, next_attempt_at, published_at, failure_category FROM socalytics.outbox_messages WHERE id = @id",
            new { id }, cancellationToken: ct));
        return ((string)r.state, (int)r.attempt_count, (DateTimeOffset?)r.next_attempt_at, (DateTimeOffset?)r.published_at, (string?)r.failure_category);
    }

    private static async Task<int> PublishAsync(AnalysisTestHost host, CancellationToken ct)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<PublishOutboxBatch>().HandleAsync(ct);
    }

    private async Task<(IsolatedDatabase Db, AnalysisTestHost Host)> StartAsync(Action<IServiceCollection>? configure, CancellationToken ct)
    {
        var db = await MigratedAsync(ct);
        var host = new AnalysisTestHost(db, nats, rustFs, configureServices: configure);
        await host.StartAsync(ct);
        return (db, host);
    }

    [Fact]
    public async Task Pending_message_is_published_with_its_id()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, host) = await StartAsync(null, ct);
        await using var _ = db;
        await using var __ = host;
        var id = await InsertPendingAsync(db, host.Time.GetUtcNow(), ct);
        host.Time.Advance(TimeSpan.FromSeconds(3));
        (await PublishAsync(host, ct)).ShouldBeGreaterThanOrEqualTo(1);
        var row = await RowAsync(db, id, ct);
        row.State.ShouldBe("published");
        (row.Published!.Value - host.Time.GetUtcNow().AddSeconds(-3)).ShouldBe(TimeSpan.FromSeconds(3));
        var messages = await host.ReadStreamAsync("SOCALYTICS_ANALYSIS_JOBS", ct);
        messages.ShouldContain(m => m.MessageId == id.ToString());
    }

    [Fact]
    public async Task Stopped_transport_backs_off_then_fails_at_the_limit()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, host) = await StartAsync(
            s => s.Configure<OutboxPublicationOptions>(o => { o.MaxAttempts = 3; o.BackoffBaseSeconds = 2; }), ct);
        await using var _ = db;
        await using var __ = host;
        var id = await InsertPendingAsync(db, host.Time.GetUtcNow(), ct);
        await nats.StopAsync();
        try
        {
            await PublishAsync(host, ct);
            var r1 = await RowAsync(db, id, ct);
            r1.State.ShouldBe("pending");
            r1.Attempts.ShouldBe(1);
            new[] { "transport-unavailable", "publish-timeout" }.ShouldContain(r1.Category);
            r1.Next.ShouldBe(host.Time.GetUtcNow().AddSeconds(2));

            (await PublishAsync(host, ct)).ShouldBe(0);
            host.Time.Advance(TimeSpan.FromSeconds(2));
            await PublishAsync(host, ct);
            var r2 = await RowAsync(db, id, ct);
            r2.Attempts.ShouldBe(2);
            r2.Next.ShouldBe(host.Time.GetUtcNow().AddSeconds(4));

            host.Time.Advance(TimeSpan.FromSeconds(4));
            await PublishAsync(host, ct);
            var r3 = await RowAsync(db, id, ct);
            r3.State.ShouldBe("failed");
            r3.Attempts.ShouldBe(3);
        }
        finally
        {
            await nats.StartAsync();
        }
    }

    [Fact]
    public async Task Publish_then_throw_republishes_as_duplicate_and_row_is_published()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, host) = await StartAsync(s =>
        {
            s.AddSingleton<NatsJetStreamTransport>();
            s.Replace(ServiceDescriptor.Singleton<IMessageTransport>(sp => new ThrowAfterPublishTransport(sp.GetRequiredService<NatsJetStreamTransport>())));
        }, ct);
        await using var _ = db;
        await using var __ = host;
        var id = await InsertPendingAsync(db, host.Time.GetUtcNow(), ct);
        await PublishAsync(host, ct);
        (await RowAsync(db, id, ct)).State.ShouldBe("pending");
        host.Time.Advance(TimeSpan.FromSeconds(5));
        var claimed = await PublishAsync(host, ct);
        var after = await RowAsync(db, id, ct);
        after.State.ShouldBe("published", $"claimed={claimed} attempts={after.Attempts} cat={after.Category} next={after.Next} now={host.Time.GetUtcNow()}");
        (await host.ReadStreamAsync("SOCALYTICS_ANALYSIS_JOBS", ct)).Count(m => m.MessageId == id.ToString()).ShouldBe(1);
    }

    [Fact]
    public async Task Concurrent_batches_never_publish_a_row_twice()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, host) = await StartAsync(null, ct);
        await using var _ = db;
        await using var __ = host;
        var ids = new List<Guid>();
        for (var i = 0; i < 20; i++)
        {
            ids.Add(await InsertPendingAsync(db, host.Time.GetUtcNow(), ct));
        }

        var counts = await Task.WhenAll(PublishAsync(host, ct), PublishAsync(host, ct));
        counts.Sum().ShouldBe(20);
        foreach (var id in ids)
        {
            (await RowAsync(db, id, ct)).State.ShouldBe("published");
        }
    }

    private sealed class ThrowAfterPublishTransport(IMessageTransport inner) : IMessageTransport
    {
        private int _thrown;

        public async Task<TransportPublishResult> PublishAsync(TransportMessage message, CancellationToken cancellationToken)
        {
            var result = await inner.PublishAsync(message, cancellationToken);
            if (Interlocked.Exchange(ref _thrown, 1) == 0)
            {
                throw new MessageTransportException(TransportFailureCategories.PublishTimeout);
            }

            return result;
        }
    }
}
