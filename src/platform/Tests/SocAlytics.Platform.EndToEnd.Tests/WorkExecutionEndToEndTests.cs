using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.Analysis;
using SocAlytics.Platform.Application.Analysis.Stores;
using SocAlytics.Platform.Domain.Analysis;
using SocAlytics.Platform.EndToEnd.Tests.Support;
using Xunit;

namespace SocAlytics.Platform.EndToEnd.Tests;

[Collection("EndToEnd")]
public sealed class WorkExecutionEndToEndTests
{
    private static readonly string Digest = "sha-256:" + new string('a', 64);

    [Fact]
    public async Task A_ready_job_is_pulled_claimed_heartbeated_and_completed_by_the_Manager()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var ct = cts.Token;
        var started = await EndToEndHarness.StartAsync(ct);
        if (started is null)
        {
            Assert.Skip("SoftHSM2 (libsofthsm2 and softhsm2-util) is required on Linux for the Manager's device key.");
            return;
        }

        await using var harness = started;
        await harness.StartManagerAsync(ct);

        var runId = await SeedReadyJobAsync(harness, ct);

        // SC-001: accepted within 30 seconds of readiness plus the configured simulated run time.
        try
        {
            await EndToEndHarness.WaitAsync(
                async () => await CountAcceptedAsync(harness, runId, ct) > 0,
                TimeSpan.FromSeconds(30) + EndToEndHarness.RunDuration, "No accepted result arrived in time.", ct);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException("No accepted result arrived in time.\n" + await DiagnosticsAsync(harness, runId, ct));
        }

        await using var sql = new NpgsqlConnection(harness.Database.MigratorConnectionString);
        await sql.OpenAsync(ct);
        await using var command = new NpgsqlCommand(
            """
            SELECT r.analyst_profile_digest, r.image_digest, a.manager_id::text, a.state, j.state
            FROM socalytics.analysis_accepted_results r
            JOIN socalytics.analysis_execution_attempts a ON a.id = r.attempt_id
            JOIN socalytics.analysis_logical_jobs j ON j.id = r.logical_job_id
            WHERE r.run_id = @runId
            """, sql);
        command.Parameters.AddWithValue("runId", runId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        (await reader.ReadAsync(ct)).ShouldBeTrue();
        reader.GetString(0).ShouldBe(Digest);
        reader.GetString(1).ShouldBe(Digest);
        reader.GetString(2).ShouldBe(harness.Control.GetStatus().ManagerId);
        reader.GetString(3).ShouldBe("Accepted");
        reader.GetString(4).ShouldBe("Completed");
        (await reader.ReadAsync(ct)).ShouldBeFalse("Exactly one accepted result is expected.");
        await reader.DisposeAsync();

        // The accepted result carries the validated manifest record.
        await using var manifests = new NpgsqlCommand(
            "SELECT count(*) FROM socalytics.analysis_accepted_result_manifests m JOIN socalytics.analysis_accepted_results r ON r.id = m.accepted_result_id WHERE r.run_id = @runId", sql);
        manifests.Parameters.AddWithValue("runId", runId);
        ((long)(await manifests.ExecuteScalarAsync(ct))!).ShouldBe(1);

        // SC-003: the manifest and artifact were stored by the Manager under the result location, never through the platform.
        await using var reference = new NpgsqlCommand("SELECT manifest_reference FROM socalytics.analysis_accepted_results WHERE run_id = @runId", sql);
        reference.Parameters.AddWithValue("runId", runId);
        var manifestReference = (string)(await reference.ExecuteScalarAsync(ct))!;
        var location = manifestReference[..(manifestReference.LastIndexOf('/') + 1)];
        using var s3 = harness.Store.CreateS3Client();
        var listed = await s3.ListObjectsV2Async(
            new Amazon.S3.Model.ListObjectsV2Request { BucketName = harness.Store.Bucket, Prefix = location }, ct);
        listed.S3Objects.Select(o => o.Key).ShouldContain(manifestReference);
        listed.S3Objects.Count.ShouldBeGreaterThan(1, "The manifest and its artifact are expected.");
    }

    internal static async Task<string> DiagnosticsAsync(EndToEndHarness harness, Guid runId, CancellationToken ct)
    {
        await using var sql = new NpgsqlConnection(harness.Database.MigratorConnectionString);
        await sql.OpenAsync(ct);
        var text = new System.Text.StringBuilder();
        foreach (var query in new[]
        {
            "SELECT 'job ' || state FROM socalytics.analysis_logical_jobs WHERE run_id = @runId",
            "SELECT 'outbox ' || message_type || ' ' || COALESCE(published_at::text, 'unpublished') FROM socalytics.outbox_messages WHERE run_id = @runId",
            "SELECT 'attempt ' || a.state FROM socalytics.analysis_execution_attempts a JOIN socalytics.analysis_logical_jobs j ON j.id = a.logical_job_id WHERE j.run_id = @runId",
        })
        {
            try
            {
                await using var command = new NpgsqlCommand(query, sql);
                command.Parameters.AddWithValue("runId", runId);
                await using var reader = await command.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    text.AppendLine(reader.GetString(0));
                }
            }
            catch (PostgresException e)
            {
                text.AppendLine(e.MessageText);
            }
        }

        text.AppendLine("manager " + System.Text.Json.JsonSerializer.Serialize(harness.Control.GetStatus()));
        foreach (var entry in harness.ManagerLogs.Entries.Where(e => e.Level >= Microsoft.Extensions.Logging.LogLevel.Warning).TakeLast(30))
        {
            text.AppendLine(entry.ToString());
        }

        return text.ToString();
    }

    internal static async Task<long> CountAcceptedAsync(EndToEndHarness harness, Guid runId, CancellationToken ct)
    {
        await using var sql = new NpgsqlConnection(harness.Database.MigratorConnectionString);
        await sql.OpenAsync(ct);
        await using var command = new NpgsqlCommand("SELECT count(*) FROM socalytics.analysis_accepted_results WHERE run_id = @runId", sql);
        command.Parameters.AddWithValue("runId", runId);
        return (long)(await command.ExecuteScalarAsync(ct))!;
    }

    /// <summary>Creates a run with one ready match-scope job and lets readiness enqueue its outbox message.</summary>
    internal static async Task<Guid> SeedReadyJobAsync(
        EndToEndHarness harness, CancellationToken ct, int leaseSeconds = 60, int staleTimeoutSeconds = 120, Guid? teamId = null, Guid? matchId = null)
    {
        var node = new WorkflowNodeRecord(
            Guid.CreateVersion7(), "e2e-match-node", EndToEndHarness.MatchCapability, "1.0.0", CapabilityTier.LowLevel, ExecutionScope.Match, 0,
            WorkflowNodeState.Waiting, null, default, "{\"analystProfileId\":\"profile\",\"policyVersions\":{}}");
        await using var scope = harness.Platform.Services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        await using var uow = await sp.GetRequiredService<IUnitOfWork>().BeginAsync(ct);
        var runId = await sp.GetRequiredService<IAnalysisRunStore>().InsertAsync(new NewAnalysisRun(
            teamId ?? Guid.NewGuid(), matchId ?? Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow, "development-default", "1", [EndToEndHarness.MatchCapability], "all-requested-capabilities",
            AnalysisRunState.Requested, null, new AnalysisRunSnapshotRecord("{}", Digest), [node], []), ct);
        await sp.GetRequiredService<ILogicalJobStore>().InsertAsync(
        [
            new NewLogicalJob(
                Guid.CreateVersion7(), runId, node.Id, ExecutionScope.Match, null, null, null, null, LogicalJobState.Waiting,
                new LogicalJobSnapshotRecord(3, leaseSeconds, staleTimeoutSeconds, 600, Digest, Digest, null, "{\"cpu\":\"1\",\"memory\":\"1Gi\"}", "1.0.0")),
        ], ct);
        await uow.CommitAsync(ct);

        await using var applyScope = harness.Platform.Services.CreateAsyncScope();
        await using var apply = await applyScope.ServiceProvider.GetRequiredService<IUnitOfWork>().BeginAsync(ct);
        await applyScope.ServiceProvider.GetRequiredService<IAnalysisRunStore>().LockRunAsync(runId, ct);
        await applyScope.ServiceProvider.GetRequiredService<RunReadinessCoordinator>().ApplyAsync(runId, ct);
        await apply.CommitAsync(ct);
        return runId;
    }
}
