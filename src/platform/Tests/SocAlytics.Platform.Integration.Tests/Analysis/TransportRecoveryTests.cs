using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using NATS.Client.JetStream;
using SocAlytics.Platform.Integration.Tests.IdentityAccess.Support;
using SocAlytics.Platform.Integration.Tests.Recordings;
using SocAlytics.Platform.Migrator;
using Shouldly;
using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Infrastructure.Messaging.Nats;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using Xunit;
using Npgsql;
using SocAlytics.Platform.Application.Analysis.Commands;
using SocAlytics.Platform.Application.Messaging;
using SocAlytics.Platform.Integration.Tests.Analysis.Support;

namespace SocAlytics.Platform.Integration.Tests.Analysis;

[Collection("NatsFaultInjection")]
public sealed class TransportRecoveryTests(NatsContainerFixture nats, PostgresContainerFixture postgres, RustFsContainerFixture rustFs)
{
    private async Task<(IsolatedDatabase Db, PlatformApiFactory Factory)> StartHostAsync(string? natsConnection, CancellationToken ct)
    {
        var db = await postgres.CreateDatabaseAsync(ct);
        (await MigratorHarness.RunAsync(db, TestMigrationCatalogs.Platform(), ct)).ExitCode.ShouldBe(MigratorExitCode.Success);
        var factory = new PlatformApiFactory(
            db,
            clubBootstrap: new Dictionary<string, string?>
            {
                ["ClubDisplayName"] = "Transport Club",
                ["FirstClubAdmin:AccountName"] = "transport-admin",
                ["FirstClubAdmin:InitialPassword"] = "Transport-Initial-Pass-1234",
            },
            recordingUpload: RecordingTestData.WithKeyPrefix(RecordingTestData.NewKeyPrefix()),
            objectStorage: new Dictionary<string, string?>
            {
                ["ServiceUrl"] = rustFs.ServiceUrl,
                ["Region"] = rustFs.Region,
                ["AccessKey"] = rustFs.AccessKey,
                ["SecretKey"] = rustFs.SecretKey,
                ["Bucket"] = rustFs.Bucket,
            },
            extraSettings: natsConnection is null ? null : new Dictionary<string, string?>
            {
                ["ConnectionStrings:nats"] = natsConnection,
                ["Messaging:Nats:ProvisionTopology"] = "true",
            });

        return (db, factory);
    }

    private static ServiceProvider Build(string? connectionString)
    {
        var settings = new Dictionary<string, string?> { ["Messaging:Nats:ProvisionTopology"] = "true" };
        if (connectionString is not null)
        {
            settings["ConnectionStrings:nats"] = connectionString;
        }

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        services.AddOptions<NatsOptions>().BindConfiguration(NatsOptions.SectionName);
        services.AddSingleton<NatsConnectionProvider>();
        services.AddSingleton<NatsTopology>();
        services.AddSingleton<IHostedService, NatsConnectionStarter>();
        services.AddSingleton<IMessageTransport, NatsJetStreamTransport>();
        return services.BuildServiceProvider();
    }

    private static readonly TimeSpan RetryLimit = TimeSpan.FromSeconds(120);

    // Retries only a call that timed out under load; the checks on its result stay with the caller.
    private static async Task<T> RetryTimedOutAsync<T>(Func<Task<T>> call, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + RetryLimit;
        while (true)
        {
            try
            {
                return await call();
            }
            catch (Exception ex) when (!ct.IsCancellationRequested
                && DateTime.UtcNow < deadline
                && (ex is NatsJSApiNoResponseException or TimeoutException
                    || ex is MessageTransportException { FailureCategory: "publish-timeout" }))
            {
                await Task.Delay(500, ct);
            }
        }
    }

    private static async Task RetryTimedOutAsync(Func<Task> call, CancellationToken ct) =>
        await RetryTimedOutAsync<bool>(async () =>
        {
            await call();
            return true;
        }, ct);

    private static async Task<HealthReportEntry> NatsEntryAsync(PlatformApiFactory factory, CancellationToken ct)
    {
        var report = await factory.Services.GetRequiredService<HealthCheckService>()
            .CheckHealthAsync(r => r.Name == "nats", ct);
        return report.Entries["nats"];
    }

    [Fact]
    public async Task Without_connection_string_no_nats_check_is_registered()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, factory) = await StartHostAsync(null, ct);
        await using var _ = db;
        await using var __ = factory;
        factory.Services.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value
            .Registrations.ShouldNotContain(r => r.Name == "nats");
        using var client = factory.CreateApiClient();
        (await client.GetAsync("/health", ct)).StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);
        (await client.GetStringAsync("/health", ct)).ShouldBe("Healthy");
        (await client.GetAsync("/alive", ct)).StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);
    }

    [Fact]
    public async Task Health_follows_the_connection_and_signals_reconnect()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, factory) = await StartHostAsync(nats.ConnectionString, ct);
        await using var _ = db;
        await using var __ = factory;
        using var client = factory.CreateApiClient();
        var signal = factory.Services.GetRequiredService<TransportReconnectSignal>();
        var initial = DateTime.UtcNow.AddSeconds(120);
        while ((await NatsEntryAsync(factory, ct)).Status != HealthStatus.Healthy && DateTime.UtcNow < initial)
        {
            await Task.Delay(200, ct);
        }

        (await NatsEntryAsync(factory, ct)).Status.ShouldBe(HealthStatus.Healthy);

        var before = signal.Count;
        await nats.StopAsync();
        try
        {
            var entry = await NatsEntryAsync(factory, ct);
            var deadline = DateTime.UtcNow.AddSeconds(120);
            while (entry.Status != HealthStatus.Degraded && DateTime.UtcNow < deadline)
            {
                await Task.Delay(200, ct);
                entry = await NatsEntryAsync(factory, ct);
            }

            entry.Status.ShouldBe(HealthStatus.Degraded);
            entry.Description.ShouldBe("transport-unavailable");
            using var response = await client.GetAsync("/health", ct);
            response.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);
            (await response.Content.ReadAsStringAsync(ct)).ShouldBe("Degraded");
        }
        finally
        {
            await nats.StartAsync();
        }

        var recovered = DateTime.UtcNow.AddSeconds(180);
        while (signal.Count == before && DateTime.UtcNow < recovered)
        {
            await Task.Delay(200, ct);
        }

        signal.Count.ShouldBe(before + 1);
        var healthy = DateTime.UtcNow.AddSeconds(120);
        while ((await NatsEntryAsync(factory, ct)).Status != HealthStatus.Healthy && DateTime.UtcNow < healthy)
        {
            await Task.Delay(200, ct);
        }

        (await NatsEntryAsync(factory, ct)).Status.ShouldBe(HealthStatus.Healthy);
    }

    private static TransportMessage Message(string id) =>
        new("matches.recordings-finalized", id, Encoding.UTF8.GetBytes("{\"a\":1}"));

    [Fact]
    public async Task Hosted_service_connects_without_any_publish()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var provider = Build(nats.ConnectionString);
        var connection = provider.GetRequiredService<NatsConnectionProvider>();
        foreach (var hosted in provider.GetServices<IHostedService>())
        {
            await hosted.StartAsync(ct);
        }

        var deadline = DateTime.UtcNow.AddSeconds(120);
        while (!connection.IsConnected && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100, ct);
        }

        connection.IsConnected.ShouldBeTrue();
    }

    [Fact]
    public async Task Topology_provisioning_is_idempotent()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var provider = Build(nats.ConnectionString);
        var topology = provider.GetRequiredService<NatsTopology>();
        await RetryTimedOutAsync(() => topology.EnsureAsync(ct), ct);
        await RetryTimedOutAsync(() => topology.EnsureAsync(ct), ct);

        var js = new NatsJSContext(provider.GetRequiredService<NatsConnectionProvider>().Connection);
        var stream = await RetryTimedOutAsync(async () => await js.GetStreamAsync("SOCALYTICS_EVENTS", cancellationToken: ct), ct);
        stream.Info.Config.DuplicateWindow.ShouldBe(TimeSpan.FromSeconds(120));
        await RetryTimedOutAsync(async () => await js.GetStreamAsync("SOCALYTICS_ANALYSIS_JOBS", cancellationToken: ct), ct);
    }

    [Fact]
    public async Task Publish_lands_on_the_stream_and_a_repeated_id_is_a_duplicate()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var provider = Build(nats.ConnectionString);
        var transport = provider.GetRequiredService<IMessageTransport>();

        // A timed-out publish may still have landed, so each attempt of the first publish uses a fresh id.
        var (id, first) = await RetryTimedOutAsync(async () =>
        {
            var attempt = Guid.NewGuid().ToString();
            return (attempt, await transport.PublishAsync(Message(attempt), ct));
        }, ct);
        first.ShouldBe(TransportPublishResult.Published);
        (await RetryTimedOutAsync(() => transport.PublishAsync(Message(id), ct), ct)).ShouldBe(TransportPublishResult.Duplicate);

        var js = new NatsJSContext(provider.GetRequiredService<NatsConnectionProvider>().Connection);
        var stream = await RetryTimedOutAsync(async () => await js.GetStreamAsync("SOCALYTICS_EVENTS", cancellationToken: ct), ct);
        (stream.Info.State.Messages > 0).ShouldBeTrue();
    }

    [Fact]
    public async Task Missing_connection_string_yields_transport_unavailable()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var provider = Build(null);
        provider.GetRequiredService<NatsConnectionProvider>().IsConfigured.ShouldBeFalse();
        foreach (var hosted in provider.GetServices<IHostedService>())
        {
            await hosted.StartAsync(ct);
        }

        var ex = await Should.ThrowAsync<MessageTransportException>(
            () => provider.GetRequiredService<IMessageTransport>().PublishAsync(Message("x"), ct));
        ex.FailureCategory.ShouldBe("transport-unavailable");
    }

    [Fact]
    public async Task Stopped_container_yields_transport_unavailable()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var provider = Build(nats.ConnectionString);
        var transport = provider.GetRequiredService<IMessageTransport>();
        await RetryTimedOutAsync(() => transport.PublishAsync(Message(Guid.NewGuid().ToString()), ct), ct);

        await nats.StopAsync();
        try
        {
            var ex = await Should.ThrowAsync<MessageTransportException>(
                () => transport.PublishAsync(Message(Guid.NewGuid().ToString()), ct));
            ex.FailureCategory.ShouldBeOneOf("transport-unavailable", "publish-timeout");
        }
        finally
        {
            await nats.StartAsync();
        }
    }

    private static async Task<long> ScalarAsync(IsolatedDatabase db, string sql, CancellationToken ct)
    {
        await using var c = new NpgsqlConnection(db.MigratorConnectionString);
        await c.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, c);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
    }

    private static async Task ExecAsync(IsolatedDatabase db, string sql, CancellationToken ct)
    {
        await using var c = new NpgsqlConnection(db.MigratorConnectionString);
        await c.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, c);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<(Guid Job, Guid Occ, Guid Message)> SeedAsync(IsolatedDatabase db, string state, string messageState, CancellationToken ct)
    {
        Guid run = Guid.NewGuid(), node = Guid.NewGuid(), job = Guid.NewGuid(), occ = Guid.NewGuid(), msg = Guid.NewGuid(), g = Guid.NewGuid();
        var digest = "sha-256:" + new string('a', 64);
        await ExecAsync(db, $$"""
            INSERT INTO socalytics.analysis_runs (id, team_id, match_id, recording_set_version_id, recording_set_finalized_at, workflow_id, workflow_version, requested_capabilities, completion_policy, state, created_at, running_at, updated_at)
            VALUES ('{{run}}', '{{g}}', '{{g}}', '{{g}}', now(), 'wf', '1.0.0', ARRAY['cap-a'], 'all-requested-capabilities', 'Running', now(), now(), now());
            INSERT INTO socalytics.analysis_workflow_nodes (id, run_id, node_key, capability_id, capability_version, tier, execution_scope, ordinal, state, updated_at)
            VALUES ('{{node}}', '{{run}}', 'm1', 'cap-a', '1.0.0', 'high-level', 'match', 0, 'Active', now());
            INSERT INTO socalytics.analysis_run_snapshots (run_id, snapshot, snapshot_digest) VALUES ('{{run}}', '{}'::jsonb, 'sha-256:a');
            INSERT INTO socalytics.analysis_node_snapshots (node_id, run_id, snapshot) VALUES ('{{node}}', '{{run}}', '{"analystProfileId":"profile","policyVersions":{ } }'::jsonb);
            INSERT INTO socalytics.analysis_logical_jobs (id, run_id, node_id, execution_scope, state, updated_at) VALUES ('{{job}}', '{{run}}', '{{node}}', 'match', 'Waiting', now());
            INSERT INTO socalytics.analysis_logical_job_snapshots (logical_job_id, max_attempts, lease_seconds, stale_timeout_seconds, execution_timeout_seconds, analyst_profile_digest, image_digest, model_digest, resources, schema_version)
            VALUES ('{{job}}', 2, 30, 90, 600, '{{digest}}', '{{digest}}', '{{digest}}', '{}'::jsonb, '1.0.0');
            INSERT INTO socalytics.analysis_readiness_occurrences (id, logical_job_id, sequence, cause, created_at) VALUES ('{{occ}}', '{{job}}', 1, 'initial', now());
            UPDATE socalytics.analysis_logical_jobs SET state = '{{state}}', current_readiness_occurrence_id = '{{occ}}' WHERE id = '{{job}}';
            INSERT INTO socalytics.outbox_messages (id, message_type, contract_version, subject, payload, causation_id, state, created_at, published_at)
            VALUES ('{{msg}}', 'analysis.ready-work', '1.0.0', 'analysis.jobs.ready.cap-a', '{"a":1}'::jsonb, '{{occ}}', '{{messageState}}', now(), CASE WHEN '{{messageState}}' = 'published' THEN now() END);
            """, ct);
        return (job, occ, msg);
    }

    [Fact]
    public async Task Reconciliation_republishes_only_ready_work_and_pending_notifications_idempotently()
    {
        var ct = TestContext.Current.CancellationToken;
        var db = await postgres.CreateDatabaseAsync(ct);
        await using var _ = db;
        (await MigratorHarness.RunAsync(db, TestMigrationCatalogs.Platform(), ct)).ExitCode.ShouldBe(MigratorExitCode.Success);
        var host = new AnalysisTestHost(db, nats, rustFs);
        await using var __ = host;
        await host.StartAsync(ct);

        var ready = await SeedAsync(db, "Ready", "published", ct);
        var claimed = await SeedAsync(db, "Claimed", "published", ct);
        var others = new List<(Guid Job, Guid Occ, Guid Message)>();
        foreach (var state in new[] { "Completed", "Blocked", "Failed", "Waiting" })
        {
            others.Add(await SeedAsync(db, state, "published", ct));
        }

        var notification = Guid.NewGuid();
        var failedNotification = Guid.NewGuid();
        var unreceived = Guid.NewGuid();
        await ExecAsync(db, $$"""
            INSERT INTO socalytics.outbox_messages (id, message_type, contract_version, subject, payload, causation_id, state, created_at)
            VALUES ('{{notification}}', 'analysis.run-state-changed', '1.0.0', 'analysis.events.run-state-changed', '{"a":1}'::jsonb, '{{notification}}', 'pending', now()),
                   ('{{failedNotification}}', 'matches.recordings-finalized', '1.0.0', 'matches.recordings-finalized', '{"a":1}'::jsonb, '{{failedNotification}}', 'failed', now()),
                   ('{{unreceived}}', 'matches.recordings-finalized', '1.0.0', 'matches.recordings-finalized', '{"a":1}'::jsonb, '{{unreceived}}', 'published', now());
            """, ct);

        await nats.ReplaceAsync();
        async Task<ReconciliationResult> RunAsync()
        {
            await using var scope = host.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<ReconcileAnalysisPublicationHandler>().HandleAsync(ct);
        }

        var result = await RunAsync();
        result.ReadyJobsRepublished.ShouldBe(1);
        result.NotificationsRepublished.ShouldBe(3);
        (await ScalarAsync(db, $"SELECT publication_round FROM socalytics.outbox_messages WHERE id = '{ready.Message}'", ct)).ShouldBe(2);
        (await ScalarAsync(db, $"SELECT count(*) FROM socalytics.outbox_messages WHERE id = '{ready.Message}' AND state = 'pending' AND attempt_count = 0 AND next_attempt_at IS NULL", ct)).ShouldBe(1);
        foreach (var other in others.Append(claimed))
        {
            (await ScalarAsync(db, $"SELECT count(*) FROM socalytics.outbox_messages WHERE id = '{other.Message}' AND state = 'published' AND publication_round = 1", ct)).ShouldBe(1);
        }

        await RunAsync();
        (await ScalarAsync(db, $"SELECT count(*) FROM socalytics.outbox_messages WHERE state = 'pending'", ct)).ShouldBe(4);

        using var manager = new TestManagerClient(host.Factory);
        using var stale = await manager.ClaimAsync(claimed.Job, claimed.Occ, "stale-key", ct: ct);
        stale.StatusCode.ShouldBe(System.Net.HttpStatusCode.Conflict);
        (await stale.Content.ReadAsStringAsync(ct)).ShouldContain("claim-obsolete");
        (await ScalarAsync(db, "SELECT count(*) FROM socalytics.analysis_execution_attempts", ct)).ShouldBe(0);

        await using (var scope = host.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<PublishOutboxBatch>().HandleAsync(ct);
        }

        (await ScalarAsync(db, "SELECT count(*) FROM socalytics.outbox_messages WHERE state = 'pending'", ct)).ShouldBe(0);
        var published = await host.ReadStreamAsync("SOCALYTICS_ANALYSIS_JOBS", ct);
        published.Count(m => m.MessageId == $"{ready.Message}.r3").ShouldBe(1);
        published.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Reconnection_triggers_reconciliation_automatically()
    {
        var ct = TestContext.Current.CancellationToken;
        var db = await postgres.CreateDatabaseAsync(ct);
        await using var _ = db;
        (await MigratorHarness.RunAsync(db, TestMigrationCatalogs.Platform(), ct)).ExitCode.ShouldBe(MigratorExitCode.Success);
        var host = new AnalysisTestHost(db, nats, rustFs, workersEnabled: true);
        await using var __ = host;
        await host.StartAsync(ct);
        var ready = await SeedAsync(db, "Ready", "published", ct);
        var signal = host.Services.GetRequiredService<TransportReconnectSignal>();
        var before = signal.Count;

        await nats.ReplaceAsync();
        var deadline = DateTime.UtcNow.AddSeconds(240);
        while (DateTime.UtcNow < deadline
            && await ScalarAsync(db, $"SELECT count(*) FROM socalytics.outbox_messages WHERE id = '{ready.Message}' AND publication_round > 1 AND state = 'published'", ct) == 0)
        {
            host.Time.Advance(TimeSpan.FromSeconds(3));
            await Task.Delay(500, ct);
        }

        signal.Count.ShouldBeGreaterThan(before);
        (await ScalarAsync(db, $"SELECT count(*) FROM socalytics.outbox_messages WHERE id = '{ready.Message}' AND publication_round > 1 AND state = 'published'", ct)).ShouldBe(1);
    }
}
