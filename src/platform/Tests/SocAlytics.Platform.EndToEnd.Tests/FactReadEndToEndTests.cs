using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.Analysis;
using SocAlytics.Platform.Application.Analysis.Stores;
using SocAlytics.Platform.Domain.Analysis;
using SocAlytics.Platform.EndToEnd.Tests.Support;
using SocAlytics.Platform.Integration.Tests.IdentityAccess.Support;
using SocAlytics.Platform.Integration.Tests.Recordings.Support;
using Xunit;

namespace SocAlytics.Platform.EndToEnd.Tests;

[Collection("EndToEnd")]
public sealed class FactReadEndToEndTests
{
    private const int Segments = 2;
    private const long SegmentMs = 300_000;
    private const long FactsPerResult = 3;
    private static readonly string Digest = "sha-256:" + new string('a', 64);

    [Fact]
    public async Task A_team_member_reads_every_accepted_fact_of_the_run_exactly_once_with_accepted_coverage_for_the_whole_match()
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
        var hierarchy = await new ClubHierarchyBuilder(harness.Platform, harness.Admin).CreateAsync(ct);
        var coachId = await TestMembers.SeedAsync(harness.Database, "coach-e2e", cancellationToken: ct);
        await ExecuteAsync(
            harness, "INSERT INTO socalytics.team_role_assignment (member_account_id, team_id, role, assigned_at, assigned_by_account_id) VALUES (@m, @t, 'coach', now(), @m)",
            [("m", coachId), ("t", hierarchy.TeamId)], ct);
        var setId = await SeedRecordingSetAsync(harness, hierarchy, coachId, ct);
        using var coach = await ApiSession.SignInAsync(harness.Platform, "coach-e2e", TestMembers.DefaultPassword, ct);

        await harness.StartManagerAsync(ct, TimeSpan.FromSeconds(1));
        var runId = await SeedRunAsync(harness, hierarchy, setId, ct);

        try
        {
            await EndToEndHarness.WaitAsync(
                async () => await WorkExecutionEndToEndTests.CountAcceptedAsync(harness, runId, ct) == Segments + 1,
                TimeSpan.FromSeconds(120), "The segment results and the match result were not all accepted.", ct);
        }
        catch (TimeoutException e)
        {
            throw new TimeoutException(e.Message + "\n" + await WorkExecutionEndToEndTests.DiagnosticsAsync(harness, runId, ct));
        }

        using var streamsResponse = await coach.Client.GetAsync($"/api/v1/analysis/runs/{runId}/fact-streams", ct);
        streamsResponse.StatusCode.ShouldBe(HttpStatusCode.OK, await streamsResponse.Content.ReadAsStringAsync(ct));
        var streams = (await streamsResponse.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("items").EnumerateArray().ToList();
        streams.Count.ShouldBe(2);

        var read = new List<(Guid AcceptedResultId, int Ordinal)>();
        foreach (var stream in streams)
        {
            var capability = stream.GetProperty("capability");
            var accepted = stream.GetProperty("coverage").GetProperty("accepted").GetInt32();
            var expectedFacts = (stream.GetProperty("executionScope").GetString() == "segment" ? Segments : 1) * FactsPerResult;
            stream.GetProperty("factCount").GetInt64().ShouldBe(expectedFacts);
            accepted.ShouldBe(stream.GetProperty("executionScope").GetString() == "segment" ? Segments : 1);
            foreach (var other in new[] { "missing", "failed", "blocked", "skipped" })
            {
                stream.GetProperty("coverage").GetProperty(other).GetInt32().ShouldBe(0);
            }

            var path = $"/api/v1/analysis/runs/{runId}/facts?capabilityId={capability.GetProperty("id").GetString()}" +
                $"&capabilityVersion={capability.GetProperty("version").GetString()}&schemaVersion={stream.GetProperty("schemaVersion").GetString()}" +
                $"&nodeKey={stream.GetProperty("nodeKey").GetString()}&pageSize=2";
            var streamFacts = new List<(Guid, int)>();
            string? token = null;
            var pages = 0;
            do
            {
                using var response = await coach.Client.GetAsync(token is null ? path : path + "&continuationToken=" + Uri.EscapeDataString(token), ct);
                var text = await response.Content.ReadAsStringAsync(ct);
                response.StatusCode.ShouldBe(HttpStatusCode.OK, text);
                var page = JsonDocument.Parse(text).RootElement;
                pages++;

                var windows = page.GetProperty("coverage").EnumerateArray().ToList();
                windows.ShouldNotBeEmpty();
                windows.ShouldAllBe(w => w.GetProperty("state").GetString() == "accepted");
                windows[0].GetProperty("startMs").GetInt64().ShouldBe(0);
                windows[^1].GetProperty("endMs").GetInt64().ShouldBe(Segments * SegmentMs);

                streamFacts.AddRange(page.GetProperty("items").EnumerateArray()
                    .Select(i => (i.GetProperty("acceptedResultId").GetGuid(), i.GetProperty("ordinal").GetInt32())));
                token = page.TryGetProperty("continuationToken", out var next) && next.ValueKind == JsonValueKind.String ? next.GetString() : null;
                pages.ShouldBeLessThan(20);
            }
            while (token is not null);

            streamFacts.Count.ShouldBe((int)expectedFacts);
            pages.ShouldBeGreaterThan(1, "A page size of two splits the stream into several pages.");
            read.AddRange(streamFacts);
        }

        read.Distinct().Count().ShouldBe(read.Count, "Every fact is read exactly once.");
        read.Count.ShouldBe((int)((Segments + 1) * FactsPerResult));
        var acceptedIds = await AcceptedIdsAsync(harness, runId, ct);
        read.Select(f => f.AcceptedResultId).Distinct().Order().ShouldBe(acceptedIds.Order());
    }

    internal static async Task<Guid> SeedRecordingSetAsync(EndToEndHarness harness, ClubHierarchy hierarchy, Guid createdBy, CancellationToken ct)
    {
        var (session, version, mapping, set) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var hex = new string('a', 64);
        var spans = $"{{\"spans\":[{{\"mediaStartMilliseconds\":0,\"mediaEndMilliseconds\":{Segments * SegmentMs},\"matchStartMilliseconds\":0}}]}}";
        (string Sql, (string, object)[] Parameters)[] inserts =
        [
            ("INSERT INTO socalytics.recording_upload_sessions (id, match_id, team_id, state, display_name, content_type, total_size_bytes, part_size_bytes, part_count, part_digests, content_digest, object_key, multipart_upload_id, created_by, created_at, expires_at, completed_at) " +
             $"VALUES (@session, @match, @team, 'completed', 'r', 'video/mp4', 10, 10, 1, decode('{hex}', 'hex'), 'sha-256-parts:10:1:{hex}', @key, @upload, @acct, now(), now(), now())",
                [("session", session), ("match", hierarchy.MatchId), ("team", hierarchy.TeamId), ("key", $"k-{session}"), ("upload", $"u-{session}"), ("acct", createdBy)]),
            ("INSERT INTO socalytics.recording_versions (id, match_id, team_id, upload_session_id, object_key, total_size_bytes, part_size_bytes, part_count, content_digest, display_name, content_type, created_by, created_at) " +
             $"VALUES (@version, @match, @team, @session, @key, 10, 10, 1, 'sha-256-parts:10:1:{hex}', 'r', 'video/mp4', @acct, now())",
                [("version", version), ("match", hierarchy.MatchId), ("team", hierarchy.TeamId), ("session", session), ("key", $"k-{session}"), ("acct", createdBy)]),
            ("INSERT INTO socalytics.recording_timeline_mappings (id, recording_version_id, match_id, spans, mapping_digest, created_by, created_at) " +
             $"VALUES (@mapping, @version, @match, @spans::jsonb, 'sha-256:{hex}', @acct, now())",
                [("mapping", mapping), ("version", version), ("match", hierarchy.MatchId), ("spans", spans), ("acct", createdBy)]),
            ("INSERT INTO socalytics.recording_set_versions (id, match_id, team_id, member_count, created_by, finalized_at) VALUES (@set, @match, @team, 1, @acct, now())",
                [("set", set), ("match", hierarchy.MatchId), ("team", hierarchy.TeamId), ("acct", createdBy)]),
            ("INSERT INTO socalytics.recording_set_members (recording_set_version_id, position, match_id, recording_version_id, timeline_mapping_id) VALUES (@set, 1, @match, @version, @mapping)",
                [("set", set), ("match", hierarchy.MatchId), ("version", version), ("mapping", mapping)]),
        ];
        foreach (var (sql, parameters) in inserts)
        {
            await ExecuteAsync(harness, sql, parameters, ct);
        }

        return set;
    }

    private static async Task<Guid> SeedRunAsync(EndToEndHarness harness, ClubHierarchy hierarchy, Guid recordingSetVersionId, CancellationToken ct)
    {
        static string NodeSnapshot(string key, string capability) =>
            $"{{\"nodeKey\":\"{key}\",\"capabilityId\":\"{capability}\",\"analystProfileId\":\"profile\",\"analystProfileDigest\":\"{Digest}\",\"imageDigest\":\"{Digest}\",\"policyVersions\":{{}},\"schemaVersion\":\"1.0.0\"}}";
        var segment = new WorkflowNodeRecord(
            Guid.CreateVersion7(), "e2e-segment-node", EndToEndHarness.SegmentCapability, "1.0.0", CapabilityTier.LowLevel, ExecutionScope.Segment, 0,
            WorkflowNodeState.Waiting, null, default, NodeSnapshot("e2e-segment-node", EndToEndHarness.SegmentCapability));
        var match = new WorkflowNodeRecord(
            Guid.CreateVersion7(), "e2e-match-node", EndToEndHarness.MatchCapability, "1.0.0", CapabilityTier.HighLevel, ExecutionScope.Match, 1,
            WorkflowNodeState.Waiting, null, default, NodeSnapshot("e2e-match-node", EndToEndHarness.MatchCapability));
        var recordingVersionId = Guid.NewGuid();

        await using var scope = harness.Platform.Services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        await using var uow = await sp.GetRequiredService<IUnitOfWork>().BeginAsync(ct);
        var runId = await sp.GetRequiredService<IAnalysisRunStore>().InsertAsync(new NewAnalysisRun(
            hierarchy.TeamId, hierarchy.MatchId, recordingSetVersionId, "development-default", "1", [EndToEndHarness.MatchCapability], "all-requested-capabilities",
            AnalysisRunState.Requested, null,
            new AnalysisRunSnapshotRecord("{\"segmentationPolicy\":{\"durationSeconds\":300}}", Digest), [segment, match],
            [new DependencyEdgeRecord(segment.Id, match.Id, DependencyKind.Required, null)]), ct);

        var snapshot = new LogicalJobSnapshotRecord(3, 60, 120, 600, Digest, Digest, null, "{\"cpu\":\"1\",\"memory\":\"1Gi\"}", "1.0.0");
        var jobs = Enumerable.Range(1, Segments).Select(n => new NewLogicalJob(
                Guid.CreateVersion7(), runId, segment.Id, ExecutionScope.Segment, recordingVersionId, Digest, Digest, n, LogicalJobState.Waiting, snapshot))
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

    private static async Task<List<Guid>> AcceptedIdsAsync(EndToEndHarness harness, Guid runId, CancellationToken ct)
    {
        await using var sql = new NpgsqlConnection(harness.Database.MigratorConnectionString);
        await sql.OpenAsync(ct);
        await using var command = new NpgsqlCommand("SELECT id FROM socalytics.analysis_accepted_results WHERE run_id = @runId", sql);
        command.Parameters.AddWithValue("runId", runId);
        var ids = new List<Guid>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            ids.Add(reader.GetGuid(0));
        }

        return ids;
    }

    internal static async Task ExecuteAsync(EndToEndHarness harness, string sql, (string Name, object Value)[] parameters, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(harness.Database.MigratorConnectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync(ct);
    }
}
