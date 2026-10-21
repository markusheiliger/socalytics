using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using SocAlytics.Analysts.Manager.Runtime;
using SocAlytics.Analysts.Manager.Runtime.Simulated;
using SocAlytics.Analysts.Manager.Work;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.Analysis;
using SocAlytics.Platform.Application.Analysis.Stores;
using SocAlytics.Platform.Domain.Analysis;
using SocAlytics.Platform.EndToEnd.Tests.Support;
using Xunit;

namespace SocAlytics.Platform.EndToEnd.Tests;

[Collection("EndToEnd")]
public sealed class UpstreamResultReadEndToEndTests
{
    private const int Segments = 2;
    private static readonly string Digest = "sha-256:" + new string('a', 64);

    [Fact]
    public async Task The_match_node_stages_its_segment_results_and_its_accepted_lineage_equals_them()
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
        var manager = await harness.StartManagerAsync(ct, TimeSpan.FromSeconds(1));
        var runId = await SeedSegmentsAndMatchAsync(harness, ct);

        await WaitAsync(harness, runId, async () => await WorkExecutionEndToEndTests.CountAcceptedAsync(harness, runId, ct) == Segments + 1,
            TimeSpan.FromSeconds(120), "The segment results and the match result were not all accepted.", ct);

        // SC-001: the accepted lineage equals the staged segment-level results.
        var segmentIds = (await QueryAsync(harness,
            "SELECT id::text FROM socalytics.analysis_accepted_results WHERE run_id = @runId AND capability_id = @capability",
            runId, EndToEndHarness.SegmentCapability, ct)).Order().ToList();
        segmentIds.Count.ShouldBe(Segments);
        var lineage = (await QueryAsync(harness,
            """
            SELECT m.upstream_lineage::text FROM socalytics.analysis_accepted_result_manifests m
            JOIN socalytics.analysis_accepted_results r ON r.id = m.accepted_result_id
            WHERE r.run_id = @runId AND r.capability_id = @capability
            """, runId, EndToEndHarness.MatchCapability, ct)).Single();
        using (var document = JsonDocument.Parse(lineage))
        {
            document.RootElement.EnumerateArray().Select(e => e.GetProperty("acceptedResultId").GetString()!).Order()
                .ShouldBe(segmentIds);
        }

        // The consumed coverage is recorded in the match result's artifact.
        var reference = (await QueryAsync(harness,
            "SELECT manifest_reference FROM socalytics.analysis_accepted_results WHERE run_id = @runId AND capability_id = @capability",
            runId, EndToEndHarness.MatchCapability, ct)).Single();
        using var s3 = harness.Store.CreateS3Client();
        using var response = await s3.GetObjectAsync(harness.Store.Bucket, reference[..(reference.LastIndexOf('/') + 1)] + "inputs.json", ct);
        using var artifact = await JsonDocument.ParseAsync(response.ResponseStream, cancellationToken: ct);
        var coverage = artifact.RootElement.GetProperty("consumedCoverage").GetProperty(EndToEndHarness.SegmentCapability).EnumerateArray().ToList();
        coverage.Count.ShouldBe(Segments);
        coverage.ShouldAllBe(c => c.GetProperty("state").GetString() == "accepted");

        // SC-007: no staged data remains on the Manager host once the attempts ended.
        await WaitAsync(harness, runId, async () => !(await AttemptIdsAsync(harness, runId, ct)).Any(a => Directory.Exists(UpstreamStager.DirectoryFor(a))),
            TimeSpan.FromSeconds(30), "Staged data remains after the attempts ended.", ct);

        // SC-004, FR-007: the simulated Analyst never receives a grant URL, a token, or a platform address.
        var adapter = (SimulatedRuntimeAdapter)manager.Services.GetRequiredService<RuntimeAdapterCatalog>().Find(SimulatedRuntimeAdapter.TestProfile)!;
        var launches = adapter.Attempts.Select(a => a.Launch).ToList();
        launches.Count.ShouldBeGreaterThanOrEqualTo(Segments + 1);
        foreach (var launch in launches)
        {
            var text = JsonSerializer.Serialize(launch);
            foreach (var forbidden in new[] { "://", "dpop", "bearer", "access_token", "accesstoken", "x-amz", harness.Store.ServiceUrl })
            {
                text.ShouldNotContain(forbidden, Case.Insensitive);
            }
        }
    }

    [Fact]
    public async Task Partial_coverage_is_recorded_and_the_lineage_equals_exactly_the_staged_accepted_results()
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
        await harness.StartManagerAsync(ct, TimeSpan.FromSeconds(1));

        // The last segment-level job is terminal without an accepted result when the match job becomes ready.
        var runId = await SeedSegmentsAndMatchAsync(harness, ct, withoutResult: 1);
        const int accepted = Segments - 1;

        await WaitAsync(harness, runId, async () => await WorkExecutionEndToEndTests.CountAcceptedAsync(harness, runId, ct) == accepted + 1,
            TimeSpan.FromSeconds(120), "The accepted segment result and the match result were not both accepted.", ct);

        var segmentIds = await QueryAsync(harness,
            "SELECT id::text FROM socalytics.analysis_accepted_results WHERE run_id = @runId AND capability_id = @capability",
            runId, EndToEndHarness.SegmentCapability, ct);
        segmentIds.Count.ShouldBe(accepted);
        var lineage = (await QueryAsync(harness,
            """
            SELECT m.upstream_lineage::text FROM socalytics.analysis_accepted_result_manifests m
            JOIN socalytics.analysis_accepted_results r ON r.id = m.accepted_result_id
            WHERE r.run_id = @runId AND r.capability_id = @capability
            """, runId, EndToEndHarness.MatchCapability, ct)).Single();
        using (var document = JsonDocument.Parse(lineage))
        {
            document.RootElement.EnumerateArray().Select(e => e.GetProperty("acceptedResultId").GetString()!).Order()
                .ShouldBe(segmentIds.Order());
        }

        var reference = (await QueryAsync(harness,
            "SELECT manifest_reference FROM socalytics.analysis_accepted_results WHERE run_id = @runId AND capability_id = @capability",
            runId, EndToEndHarness.MatchCapability, ct)).Single();
        using var s3 = harness.Store.CreateS3Client();
        using var response = await s3.GetObjectAsync(harness.Store.Bucket, reference[..(reference.LastIndexOf('/') + 1)] + "inputs.json", ct);
        using var artifact = await JsonDocument.ParseAsync(response.ResponseStream, cancellationToken: ct);
        var coverage = artifact.RootElement.GetProperty("consumedCoverage").GetProperty(EndToEndHarness.SegmentCapability).EnumerateArray().ToList();
        coverage.Count.ShouldBe(Segments);
        var states = coverage.GroupBy(c => c.GetProperty("state").GetString()).ToDictionary(g => g.Key!, g => g.Count());
        states.GetValueOrDefault("accepted").ShouldBe(accepted);
        states.Where(s => s.Key != "accepted").Sum(s => s.Value).ShouldBe(Segments - accepted);
        coverage.Single(c => c.GetProperty("state").GetString() != "accepted").GetProperty("segmentId").GetString()!
            .ShouldEndWith($"-{Segments}");
    }

    [Fact]
    public async Task A_lineage_that_ignores_the_staged_inputs_is_rejected()
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
        await harness.StartManagerAsync(ct, TimeSpan.FromSeconds(1), resultMode: SimulatedResultModes.IgnoreInputs);
        var runId = await SeedSegmentsAndMatchAsync(harness, ct);

        await WaitAsync(harness, runId,
            async () => (await QueryAsync(harness,
                "SELECT count(*)::text FROM socalytics.analysis_completion_outcomes o JOIN socalytics.analysis_logical_jobs j ON j.id = o.logical_job_id WHERE j.run_id = @runId AND o.reason = 'result-lineage-mismatch'",
                runId, null, ct)).Single() != "0",
            TimeSpan.FromSeconds(120), "The empty lineage was not rejected.", ct);

        (await WorkExecutionEndToEndTests.CountAcceptedAsync(harness, runId, ct)).ShouldBe(Segments, "Only the segment-level results are accepted.");
        (await QueryAsync(harness,
            "SELECT count(*)::text FROM socalytics.analysis_accepted_results WHERE run_id = @runId AND capability_id = @capability",
            runId, EndToEndHarness.MatchCapability, ct)).Single().ShouldBe("0");
    }

    private static async Task<Guid> SeedSegmentsAndMatchAsync(EndToEndHarness harness, CancellationToken ct, int withoutResult = 0)
    {
        const string nodeSnapshot = "{\"analystProfileId\":\"profile\",\"policyVersions\":{},\"schemaVersion\":\"1.0.0\"}";
        var segment = new WorkflowNodeRecord(
            Guid.CreateVersion7(), "e2e-segment-node", EndToEndHarness.SegmentCapability, "1.0.0", CapabilityTier.LowLevel, ExecutionScope.Segment, 0,
            WorkflowNodeState.Waiting, null, default, nodeSnapshot);
        var match = new WorkflowNodeRecord(
            Guid.CreateVersion7(), "e2e-match-node", EndToEndHarness.MatchCapability, "1.0.0", CapabilityTier.HighLevel, ExecutionScope.Match, 1,
            WorkflowNodeState.Waiting, null, default, nodeSnapshot);
        var recordingVersionId = Guid.NewGuid();

        await using var scope = harness.Platform.Services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        await using var uow = await sp.GetRequiredService<IUnitOfWork>().BeginAsync(ct);
        var runId = await sp.GetRequiredService<IAnalysisRunStore>().InsertAsync(new NewAnalysisRun(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow, "development-default", "1", [EndToEndHarness.MatchCapability], "all-requested-capabilities",
            AnalysisRunState.Requested, null,
            new AnalysisRunSnapshotRecord("{\"segmentationPolicy\":{\"durationSeconds\":300}}", Digest), [segment, match],
            [new DependencyEdgeRecord(segment.Id, match.Id, DependencyKind.Required, null)]), ct);

        var snapshot = new LogicalJobSnapshotRecord(3, 60, 120, 600, Digest, Digest, null, "{\"cpu\":\"1\",\"memory\":\"1Gi\"}", "1.0.0");
        var jobs = Enumerable.Range(1, Segments).Select(n => new NewLogicalJob(
                Guid.CreateVersion7(), runId, segment.Id, ExecutionScope.Segment, recordingVersionId, Digest, Digest, n,
                n > Segments - withoutResult ? LogicalJobState.Completed : LogicalJobState.Waiting, snapshot))
            .Append(new NewLogicalJob(
                Guid.CreateVersion7(), runId, match.Id, ExecutionScope.Match, null, null, null, null, LogicalJobState.Waiting, snapshot))
            .ToList();
        await sp.GetRequiredService<ILogicalJobStore>().InsertAsync(jobs, ct);
        await uow.CommitAsync(ct);

        await using var applyScope = harness.Platform.Services.CreateAsyncScope();
        await using var apply = await applyScope.ServiceProvider.GetRequiredService<IUnitOfWork>().BeginAsync(ct);
        await applyScope.ServiceProvider.GetRequiredService<IAnalysisRunStore>().LockRunAsync(runId, ct);
        await applyScope.ServiceProvider.GetRequiredService<RunReadinessCoordinator>().ApplyAsync(runId, ct);
        await apply.CommitAsync(ct);
        return runId;
    }

    private static async Task<List<Guid>> AttemptIdsAsync(EndToEndHarness harness, Guid runId, CancellationToken ct) =>
        (await QueryAsync(harness,
            "SELECT a.id::text FROM socalytics.analysis_execution_attempts a JOIN socalytics.analysis_logical_jobs j ON j.id = a.logical_job_id WHERE j.run_id = @runId",
            runId, null, ct)).Select(Guid.Parse).ToList();

    private static async Task<List<string>> QueryAsync(EndToEndHarness harness, string query, Guid runId, string? capability, CancellationToken ct)
    {
        await using var sql = new NpgsqlConnection(harness.Database.MigratorConnectionString);
        await sql.OpenAsync(ct);
        await using var command = new NpgsqlCommand(query, sql);
        command.Parameters.AddWithValue("runId", runId);
        if (capability is not null)
        {
            command.Parameters.AddWithValue("capability", capability);
        }

        var values = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }

    private static async Task WaitAsync(EndToEndHarness harness, Guid runId, Func<Task<bool>> condition, TimeSpan timeout, string failure, CancellationToken ct)
    {
        try
        {
            await EndToEndHarness.WaitAsync(condition, timeout, failure, ct);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException(failure + "\n" + await WorkExecutionEndToEndTests.DiagnosticsAsync(harness, runId, ct));
        }
    }
}
