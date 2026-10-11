using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using SocAlytics.Platform.Application.Messaging;
using SocAlytics.Platform.Integration.Tests.Analysis.Support;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Integration.Tests.Recordings;
using SocAlytics.Platform.Migrator;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Analysis;

[Collection("NatsFaultInjection")]
public sealed class OutboxRetentionTests(NatsContainerFixture nats, PostgresContainerFixture postgres, RustFsContainerFixture rustFs)
{
    private static async Task ExecAsync(IsolatedDatabase db, string sql, CancellationToken ct)
    {
        await using var c = new NpgsqlConnection(db.MigratorConnectionString);
        await c.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, c);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<long> CountAsync(IsolatedDatabase db, Guid id, CancellationToken ct)
    {
        await using var c = new NpgsqlConnection(db.MigratorConnectionString);
        await c.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand($"SELECT count(*) FROM socalytics.outbox_messages WHERE id = '{id}'", c);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
    }

    private static Task InsertAsync(IsolatedDatabase db, Guid id, Guid causation, string state, DateTimeOffset at, CancellationToken ct)
    {
        var ts = at.UtcDateTime.ToString("O");
        return ExecAsync(db, $$"""
            INSERT INTO socalytics.outbox_messages (id, message_type, contract_version, subject, payload, causation_id, state, created_at, published_at)
            VALUES ('{{id}}', 'analysis.ready-work', '1.0.0', 'analysis.jobs.ready.cap-a', '{}'::jsonb, '{{causation}}', '{{state}}',
                    '{{ts}}'::timestamptz, CASE WHEN '{{state}}' = 'published' THEN '{{ts}}'::timestamptz END)
            """, ct);
    }

    private static Task SeedReadyJobAsync(IsolatedDatabase db, Guid occ, CancellationToken ct)
    {
        Guid run = Guid.NewGuid(), node = Guid.NewGuid(), job = Guid.NewGuid(), g = Guid.NewGuid();
        var digest = "sha-256:" + new string('a', 64);
        return ExecAsync(db, $$"""
            INSERT INTO socalytics.analysis_runs (id, team_id, match_id, recording_set_version_id, workflow_id, workflow_version, requested_capabilities, completion_policy, state, created_at, running_at, updated_at)
            VALUES ('{{run}}', '{{g}}', '{{g}}', '{{g}}', 'wf', '1.0.0', ARRAY['cap-a'], 'all-requested-capabilities', 'Running', now(), now(), now());
            INSERT INTO socalytics.analysis_workflow_nodes (id, run_id, node_key, capability_id, capability_version, tier, execution_scope, ordinal, state, updated_at)
            VALUES ('{{node}}', '{{run}}', 'm1', 'cap-a', '1.0.0', 'high-level', 'match', 0, 'Active', now());
            INSERT INTO socalytics.analysis_run_snapshots (run_id, snapshot, snapshot_digest) VALUES ('{{run}}', '{}'::jsonb, 'sha-256:a');
            INSERT INTO socalytics.analysis_node_snapshots (node_id, run_id, snapshot) VALUES ('{{node}}', '{{run}}', '{"analystProfileId":"profile","policyVersions":{ } }'::jsonb);
            INSERT INTO socalytics.analysis_logical_jobs (id, run_id, node_id, execution_scope, state, updated_at) VALUES ('{{job}}', '{{run}}', '{{node}}', 'match', 'Waiting', now());
            INSERT INTO socalytics.analysis_logical_job_snapshots (logical_job_id, max_attempts, lease_seconds, stale_timeout_seconds, execution_timeout_seconds, analyst_profile_digest, image_digest, model_digest, resources, schema_version)
            VALUES ('{{job}}', 2, 30, 90, 600, '{{digest}}', '{{digest}}', '{{digest}}', '{}'::jsonb, '1.0.0');
            INSERT INTO socalytics.analysis_readiness_occurrences (id, logical_job_id, sequence, cause, created_at) VALUES ('{{occ}}', '{{job}}', 1, 'initial', now());
            UPDATE socalytics.analysis_logical_jobs SET state = 'Ready', current_readiness_occurrence_id = '{{occ}}' WHERE id = '{{job}}';
            """, ct);
    }

    [Fact]
    public async Task Only_expired_published_and_discarded_rows_are_removed()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await postgres.CreateDatabaseAsync(ct);
        (await MigratorHarness.RunAsync(db, TestMigrationCatalogs.Platform(), ct)).ExitCode.ShouldBe(MigratorExitCode.Success);
        await using var host = new AnalysisTestHost(db, nats, rustFs);
        await host.StartAsync(ct);

        var old = host.Time.GetUtcNow().AddHours(-200);
        var recent = host.Time.GetUtcNow().AddHours(-1);
        Guid oldPublished = Guid.NewGuid(), oldDiscarded = Guid.NewGuid(), recentPublished = Guid.NewGuid(),
            oldPending = Guid.NewGuid(), oldFailed = Guid.NewGuid(), currentReady = Guid.NewGuid(), readyOcc = Guid.NewGuid();
        await InsertAsync(db, oldPublished, Guid.NewGuid(), "published", old, ct);
        await InsertAsync(db, oldDiscarded, Guid.NewGuid(), "discarded", old, ct);
        await InsertAsync(db, recentPublished, Guid.NewGuid(), "published", recent, ct);
        await InsertAsync(db, oldPending, Guid.NewGuid(), "pending", old, ct);
        await InsertAsync(db, oldFailed, Guid.NewGuid(), "failed", old, ct);
        await SeedReadyJobAsync(db, readyOcc, ct);
        await InsertAsync(db, currentReady, readyOcc, "published", old, ct);

        await using (var scope = host.Services.CreateAsyncScope())
        {
            (await scope.ServiceProvider.GetRequiredService<PurgeOutboxMessagesHandler>().HandleAsync(ct)).ShouldBe(2);
        }

        (await CountAsync(db, oldPublished, ct)).ShouldBe(0);
        (await CountAsync(db, oldDiscarded, ct)).ShouldBe(0);
        foreach (var kept in new[] { recentPublished, oldPending, oldFailed, currentReady })
        {
            (await CountAsync(db, kept, ct)).ShouldBe(1);
        }
    }
}
