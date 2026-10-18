using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;
using Shouldly;
using SocAlytics.Analysts.Manager.Runtime.Simulated;
using SocAlytics.Platform.Application.Analysis.Commands;
using SocAlytics.Platform.Application.Recordings;
using SocAlytics.Platform.EndToEnd.Tests.Support;
using SocAlytics.Platform.Integration.Tests.IdentityAccess.Support;
using SocAlytics.Platform.Integration.Tests.Recordings.Support;
using Xunit;

namespace SocAlytics.Platform.EndToEnd.Tests;

[Collection("EndToEnd")]
public sealed class PartialCoverageEndToEndTests
{
    private const int Segments = 2;

    [Fact]
    public async Task A_failed_segment_blocks_the_complete_node_while_the_partial_node_is_accepted_with_the_failed_segment_named()
    {
        var run = await RunAsync(failFirstSegment: true);

        run.State.ShouldBe("PartiallyCompleted");
        var nodes = run.Status.GetProperty("nodes").EnumerateArray().ToDictionary(n => n.GetProperty("nodeKey").GetString()!);
        nodes["dev-segment-node"].GetProperty("jobs").GetProperty("failed").GetInt32().ShouldBe(1);
        nodes["dev-match-node"].GetProperty("state").GetString()!.ToLowerInvariant().ShouldBe("blocked");
        nodes["dev-match-node"].GetProperty("blockedByNodeKey").GetString().ShouldBe("dev-segment-node");
        nodes["dev-partial-match-node"].GetProperty("state").GetString()!.ToLowerInvariant().ShouldBe("completed");
        run.AcceptedCapabilities.Order().ToList().ShouldBe(
            [EndToEndHarness.PartialMatchCapability, EndToEndHarness.SegmentCapability], "One segment and the partial match result are accepted.");

        // The staged coverage, the accepted manifest's report, and the run status read agree for every unit.
        var staged = run.StagedCoverage.GetProperty(EndToEndHarness.SegmentCapability).EnumerateArray().ToList();
        staged.Count.ShouldBe(Segments);
        var stagedStates = staged.ToDictionary(c => c.GetProperty("segmentId").GetString()!, c => c.GetProperty("state").GetString()!);
        stagedStates.Count(s => s.Value == "accepted").ShouldBe(1);
        var failed = stagedStates.Single(s => s.Value != "accepted");
        failed.Key.ShouldEndWith("-1");
        failed.Value.ShouldBe("failed");

        var reported = run.Manifest.GetProperty("inputCoverage").EnumerateArray().Single();
        reported.GetProperty("capability").GetProperty("id").GetString().ShouldBe(EndToEndHarness.SegmentCapability);
        var reportedStates = reported.GetProperty("units").EnumerateArray()
            .ToDictionary(u => u.GetProperty("segmentId").GetString()!, u => u.GetProperty("state").GetString()!);
        reportedStates.ShouldBe(stagedStates, ignoreOrder: true);

        var coverage = nodes["dev-partial-match-node"].GetProperty("inputCoverage").EnumerateArray().Single();
        coverage.GetProperty("upstreamNodeKey").GetString().ShouldBe("dev-segment-node");
        coverage.GetProperty("coverageMode").GetString().ShouldBe("partial");
        coverage.GetProperty("accepted").GetInt32().ShouldBe(stagedStates.Count(s => s.Value == "accepted"));
        coverage.GetProperty("failed").GetInt32().ShouldBe(stagedStates.Count(s => s.Value == "failed"));
        foreach (var other in new[] { "blocked", "skipped", "missing" })
        {
            coverage.GetProperty(other).GetInt32().ShouldBe(0);
        }
    }

    [Fact]
    public async Task With_every_segment_succeeding_the_run_completes_and_every_node_completes()
    {
        var run = await RunAsync(failFirstSegment: false);

        run.State.ShouldBe("Completed");
        var nodes = run.Status.GetProperty("nodes").EnumerateArray().ToList();
        nodes.Count.ShouldBe(3);
        nodes.ShouldAllBe(n => string.Equals(n.GetProperty("state").GetString(), "Completed", StringComparison.OrdinalIgnoreCase));
        run.AcceptedCapabilities.Count.ShouldBe(Segments + 2);

        var staged = run.StagedCoverage.GetProperty(EndToEndHarness.SegmentCapability).EnumerateArray().ToList();
        staged.Count.ShouldBe(Segments);
        staged.ShouldAllBe(c => c.GetProperty("state").GetString() == "accepted");
        run.Manifest.GetProperty("inputCoverage").EnumerateArray().Single().GetProperty("units").EnumerateArray()
            .ShouldAllBe(u => u.GetProperty("state").GetString() == "accepted");

        var partial = nodes.Single(n => n.GetProperty("nodeKey").GetString() == "dev-partial-match-node")
            .GetProperty("inputCoverage").EnumerateArray().Single();
        partial.GetProperty("accepted").GetInt32().ShouldBe(Segments);
        partial.GetProperty("failed").GetInt32().ShouldBe(0);
    }

    private sealed record Outcome(
        string State, JsonElement Status, List<string> AcceptedCapabilities, JsonElement StagedCoverage, JsonElement Manifest);

    private static async Task<Outcome> RunAsync(bool failFirstSegment)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var ct = cts.Token;
        var started = await EndToEndHarness.StartAsync(ct);
        if (started is null)
        {
            Assert.Skip("SoftHSM2 (libsofthsm2 and softhsm2-util) is required on Linux for the Manager's device key.");
        }

        await using var harness = started;
        var hierarchy = await new ClubHierarchyBuilder(harness.Platform, harness.Admin).CreateAsync(ct);
        var coachId = await TestMembers.SeedAsync(harness.Database, "coach-e2e", cancellationToken: ct);
        await FactReadEndToEndTests.ExecuteAsync(
            harness, "INSERT INTO socalytics.team_role_assignment (member_account_id, team_id, role, assigned_at, assigned_by_account_id) VALUES (@m, @t, 'coach', now(), @m)",
            [("m", coachId), ("t", hierarchy.TeamId)], ct);
        var setId = await FactReadEndToEndTests.SeedRecordingSetAsync(harness, hierarchy, coachId, ct);
        using var coach = await ApiSession.SignInAsync(harness.Platform, "coach-e2e", TestMembers.DefaultPassword, ct);

        var manager = await harness.StartManagerAsync(ct, TimeSpan.FromSeconds(1));
        if (failFirstSegment)
        {
            var behavior = manager.Services.GetRequiredService<IOptions<SimulatedAnalystOptions>>().Value;
            behavior.Capabilities[EndToEndHarness.SegmentCapability].FailSegmentStartsMs.Add(0);
        }

        Guid runId;
        await using (var scope = harness.Platform.Services.CreateAsyncScope())
        {
            var lineage = (await scope.ServiceProvider.GetRequiredService<IRecordingSetLookup>().GetAsync(setId, ct))!;
            var notification = new RecordingsFinalizedNotification(
                Guid.NewGuid(), "1.0.0", DateTimeOffset.UtcNow, lineage.RecordingSetVersionId, lineage.MatchId, lineage.TeamId,
                [.. lineage.Members.Select(m => new FinalizedNotificationMember(
                    m.Position, m.RecordingVersionId, m.RecordingContentDigest, m.TimelineMappingId, m.TimelineMappingDigest))]);
            var outcome = await scope.ServiceProvider.GetRequiredService<HandleRecordingsFinalizedNotificationHandler>().HandleAsync(notification, ct);
            runId = outcome.ShouldBeOfType<NotificationOutcome.RunCreated>().RunId;
        }

        try
        {
            await EndToEndHarness.WaitAsync(
                async () => (await ScalarAsync(harness, "SELECT state FROM socalytics.analysis_runs WHERE id = @runId", runId, ct)) is "Completed" or "PartiallyCompleted" or "Failed",
                TimeSpan.FromSeconds(180), "The run did not end in time.", ct);
        }
        catch (TimeoutException e)
        {
            throw new TimeoutException(e.Message + "\n" + await WorkExecutionEndToEndTests.DiagnosticsAsync(harness, runId, ct));
        }

        using var response = await coach.Client.GetAsync($"/api/v1/analysis/runs/{runId}", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(ct));
        var status = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        var state = status.GetProperty("state").GetString()!;
        (await ScalarAsync(harness, "SELECT state FROM socalytics.analysis_runs WHERE id = @runId", runId, ct)).ShouldBe(state);

        var accepted = new List<string>();
        string? partialReference = null;
        await using (var sql = new NpgsqlConnection(harness.Database.MigratorConnectionString))
        {
            await sql.OpenAsync(ct);
            await using var command = new NpgsqlCommand(
                "SELECT capability_id, manifest_reference FROM socalytics.analysis_accepted_results WHERE run_id = @runId", sql);
            command.Parameters.AddWithValue("runId", runId);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                accepted.Add(reader.GetString(0));
                if (reader.GetString(0) == EndToEndHarness.PartialMatchCapability)
                {
                    partialReference = reader.GetString(1);
                }
            }
        }

        partialReference.ShouldNotBeNull("The partial-coverage node's result is accepted.");
        using var s3 = harness.Store.CreateS3Client();
        var staged = await ReadJsonAsync(s3, harness, partialReference[..(partialReference.LastIndexOf('/') + 1)] + "inputs.json", ct);
        var manifest = await ReadJsonAsync(s3, harness, partialReference, ct);
        return new Outcome(state, status, accepted, staged.GetProperty("consumedCoverage"), manifest);
    }

    private static async Task<JsonElement> ReadJsonAsync(Amazon.S3.IAmazonS3 s3, EndToEndHarness harness, string key, CancellationToken ct)
    {
        using var response = await s3.GetObjectAsync(harness.Store.Bucket, key, ct);
        using var document = await JsonDocument.ParseAsync(response.ResponseStream, cancellationToken: ct);
        return document.RootElement.Clone();
    }

    private static async Task<string?> ScalarAsync(EndToEndHarness harness, string query, Guid runId, CancellationToken ct)
    {
        await using var sql = new NpgsqlConnection(harness.Database.MigratorConnectionString);
        await sql.OpenAsync(ct);
        await using var command = new NpgsqlCommand(query, sql);
        command.Parameters.AddWithValue("runId", runId);
        return (string?)await command.ExecuteScalarAsync(ct);
    }
}
