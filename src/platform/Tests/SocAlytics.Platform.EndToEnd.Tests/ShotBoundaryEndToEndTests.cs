using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using SocAlytics.Analysts.Manager.Work;
using SocAlytics.Platform.Application.Analysis.Commands;
using SocAlytics.Platform.Application.Recordings;
using SocAlytics.Platform.EndToEnd.Tests.Support;
using SocAlytics.Platform.Integration.Tests;
using SocAlytics.Platform.Integration.Tests.IdentityAccess.Support;
using SocAlytics.Platform.Integration.Tests.Recordings.Support;
using SocAlytics.Platform.Integration.Tests.Registry.Support;
using Xunit;

namespace SocAlytics.Platform.EndToEnd.Tests;

/// <summary>
/// The shot-boundary Analyst image is registered by digest and runs for real on the Docker runtime against the
/// materialized segments of a recording with known hard cuts; the facts a team member reads equal the known truth
/// exactly, and the cut-free segment is analyzed with an empty fact stream (SC-001).
/// </summary>
[Collection("EndToEnd")]
public sealed class ShotBoundaryEndToEndTests : IClassFixture<AnalystImageCache>
{
    private const string Prefix = "analysts/";
    private const string WorkflowId = "shot-boundary-workflow";
    private const string NodeKey = "shot-boundary";

    private readonly AnalystImageCache _images;

    public ShotBoundaryEndToEndTests(AnalystImageCache images) => _images = images;

    [Fact]
    public async Task The_registered_shot_boundary_Analyst_reports_exactly_the_known_cuts_and_none_for_the_cut_free_segment()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(14));
        var ct = cts.Token;

        var manifest = JsonNode.Parse(await File.ReadAllBytesAsync(
            Path.Combine(ShotBoundaryAnalystImage.FindRepositoryRoot(), ShotBoundaryAnalystImage.ManifestPath), ct))!;

        var registryHost = await _images.GetRegistryHostAsync(ct);
        string? compatibilityPath = null;
        try
        {
            var settings = new Dictionary<string, string?>
            {
                ["Segments:Toolchain:FfmpegPath"] = SyntheticMedia.FfmpegPath,
                ["Segments:Toolchain:FfprobePath"] = Path.Combine(Path.GetDirectoryName(SyntheticMedia.FfmpegPath) ?? string.Empty, "ffprobe"),
                ["Registry:Images:RegistryHost"] = registryHost,
                ["Registry:Images:RepositoryPrefix"] = Prefix,
                ["Registry:Images:UseHttp"] = "true",
            };
            var started = await EndToEndHarness.StartAsync(ct, settings);
            if (started is null)
            {
                Assert.Skip("SoftHSM2 (libsofthsm2 and softhsm2-util) is required on Linux for the Manager's device key.");
                return;
            }

            await using var harness = started;
            var image = await ShotBoundaryAnalystImage.GetCachedAsync(_images, Prefix, ct);
            await RegisterAndDefineDefaultWorkflowAsync(harness, image, manifest, registryHost, ct);

            using var docker = new DockerEngine();
            compatibilityPath = await DockerRuntimeEndToEndTests.WriteCompatibilityListAsync(docker, ct);
            await harness.StartManagerAsync(ct, dockerCompatibilityListPath: compatibilityPath);
            var managerLabel = $"io.socalytics.manager={harness.Control.GetStatus().ManagerId!}";

            var runId = await FinalizeAndStartRunAsync(harness, ct);
            await WaitForAcceptedAsync(harness, runId, ct);

            using var coach = await ApiSession.SignInAsync(harness.Platform, "coach-e2e", TestMembers.DefaultPassword, ct);
            await AssertStreamAsync(coach, runId, ct);
            var facts = await ReadFactsAsync(coach, runId, string.Empty, ct);
            AssertFactsEqualTruth(facts);

            var middle = await ReadFactsAsync(coach, runId, $"&fromMs={CutMedia.SegmentSeconds * 1000}&toMs={2 * CutMedia.SegmentSeconds * 1000}", ct);
            middle.Items.ShouldBeEmpty("The cut-free segment has no fact.");
            middle.Windows.ShouldNotBeEmpty();
            middle.Windows.ShouldAllBe(w => w.GetProperty("state").GetString() == "accepted");

            var attempts = await AttemptIdsAsync(harness, runId, ct);
            await EndToEndHarness.WaitAsync(
                async () => (await docker.ListContainersAsync(managerLabel, ct)).Count == 0
                    && attempts.All(a => !Directory.Exists(UpstreamStager.DirectoryFor(a))),
                TimeSpan.FromSeconds(30), "A container, attempt directory, or staged media of an attempt remains.", ct);
        }
        finally
        {
            if (compatibilityPath is not null)
            {
                File.Delete(compatibilityPath);
            }
        }
    }

    private static async Task AssertStreamAsync(ApiSession member, Guid runId, CancellationToken ct)
    {
        using var response = await member.Client.GetAsync($"/api/v1/analysis/runs/{runId}/fact-streams", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(ct));
        var stream = (await response.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("items").EnumerateArray().Single();
        stream.GetProperty("capability").GetProperty("id").GetString().ShouldBe(ShotBoundaryAnalystImage.CapabilityId);
        stream.GetProperty("capability").GetProperty("version").GetString().ShouldBe(ShotBoundaryAnalystImage.CapabilityVersion);
        stream.GetProperty("schemaVersion").GetString().ShouldBe("1.0.0");
        stream.GetProperty("executionScope").GetString().ShouldBe("segment");
        stream.GetProperty("factCount").GetInt64().ShouldBe(CutMedia.ExpectedCuts.Count);
        var coverage = stream.GetProperty("coverage");
        coverage.GetProperty("accepted").GetInt32().ShouldBe(CutMedia.SegmentCount);
        foreach (var other in new[] { "missing", "failed", "blocked", "skipped" })
        {
            coverage.GetProperty(other).GetInt32().ShouldBe(0);
        }
    }

    private sealed record FactPages(List<JsonElement> Items, List<JsonElement> Windows);

    private static async Task<FactPages> ReadFactsAsync(ApiSession member, Guid runId, string range, CancellationToken ct)
    {
        var path = $"/api/v1/analysis/runs/{runId}/facts?capabilityId={ShotBoundaryAnalystImage.CapabilityId}" +
            $"&capabilityVersion={ShotBoundaryAnalystImage.CapabilityVersion}&schemaVersion=1.0.0&pageSize=50{range}";
        var items = new List<JsonElement>();
        var windows = new List<JsonElement>();
        string? token = null;
        var pages = 0;
        do
        {
            using var response = await member.Client.GetAsync(token is null ? path : path + "&continuationToken=" + Uri.EscapeDataString(token), ct);
            var text = await response.Content.ReadAsStringAsync(ct);
            response.StatusCode.ShouldBe(HttpStatusCode.OK, text);
            var page = JsonDocument.Parse(text).RootElement.Clone();
            if (token is null)
            {
                windows.AddRange(page.GetProperty("coverage").EnumerateArray());
            }

            items.AddRange(page.GetProperty("items").EnumerateArray());
            token = page.TryGetProperty("continuationToken", out var next) && next.ValueKind == JsonValueKind.String ? next.GetString() : null;
            pages++.ShouldBeLessThan(10);
        }
        while (token is not null);

        return new FactPages(items, windows);
    }

    private static void AssertFactsEqualTruth(FactPages facts)
    {
        facts.Windows.ShouldNotBeEmpty();
        facts.Windows.ShouldAllBe(w => w.GetProperty("state").GetString() == "accepted");
        facts.Windows[0].GetProperty("startMs").GetInt64().ShouldBe(0);
        facts.Windows[^1].GetProperty("endMs").GetInt64().ShouldBe(CutMedia.DurationSeconds * 1000L);

        var expected = CutMedia.ExpectedCuts;
        facts.Items.Count.ShouldBe(expected.Count);
        for (var i = 0; i < expected.Count; i++)
        {
            var fact = facts.Items[i];
            fact.GetProperty("kind").GetString().ShouldBe(ShotBoundaryAnalystImage.FactKind);
            fact.GetProperty("startMs").GetInt64().ShouldBe(expected[i].MatchMs);
            fact.GetProperty("endMs").GetInt64().ShouldBeGreaterThan(expected[i].MatchMs);
            fact.GetProperty("payload").GetProperty("rule").GetString().ShouldBe(expected[i].Rule);
        }

        for (var segment = 1; segment <= CutMedia.SegmentCount; segment++)
        {
            var from = (segment - 1) * CutMedia.SegmentSeconds * 1000L;
            var to = segment * CutMedia.SegmentSeconds * 1000L;
            facts.Items.Count(f => f.GetProperty("startMs").GetInt64() >= from && f.GetProperty("startMs").GetInt64() < to)
                .ShouldBe(CutMedia.CutsInSegment(segment).Count, $"Segment {segment} has the known number of cuts.");
        }

        CutMedia.CutsInSegment(2).ShouldBeEmpty();
    }

    private static async Task RegisterAndDefineDefaultWorkflowAsync(
        EndToEndHarness harness, PushedAnalystImage image, JsonNode manifest, string registryHost, CancellationToken ct)
    {
        var admin = harness.Admin;
        const string profileId = ShotBoundaryAnalystImage.ProfileId;

        using var registered = await admin.SendAsync(
            HttpMethod.Post, "/api/v1/registry/analyst-images", new { reference = image.Reference(registryHost) }, ct);
        var registeredText = await registered.Content.ReadAsStringAsync(ct);
        registered.StatusCode.ShouldBe(HttpStatusCode.Created, registeredText);
        var body = JsonDocument.Parse(registeredText).RootElement;
        var imageDigest = body.GetProperty("imageDigest").GetString();
        imageDigest.ShouldBe(image.Digest);
        var capability = body.GetProperty("capability");
        capability.GetProperty("id").GetString().ShouldBe(ShotBoundaryAnalystImage.CapabilityId);
        capability.GetProperty("version").GetString().ShouldBe(ShotBoundaryAnalystImage.CapabilityVersion);
        body.GetProperty("modelDigest").GetString().ShouldBe(ShotBoundaryAnalystImage.ModelDigest);
        var declared = JsonNode.Parse(body.GetProperty("manifest").GetRawText())!;
        JsonNode.DeepEquals(declared, manifest).ShouldBeTrue("The stored capability declaration differs from the manifest file the image carries.");
        declared["capability"]!["executionScope"]!.GetValue<string>().ShouldBe("segment");
        declared["videoAccess"]!.GetValue<string>().ShouldBe("required");
        declared["upstream"]!.AsArray().ShouldBeEmpty();
        declared["output"]!["schemaVersion"]!.GetValue<string>().ShouldBe("1.0.0");

        var content = JsonNode.Parse($$"""
            {
              "capabilityId": "{{ShotBoundaryAnalystImage.CapabilityId}}",
              "capabilityVersion": "{{ShotBoundaryAnalystImage.CapabilityVersion}}",
              "modelDigest": "{{ShotBoundaryAnalystImage.ModelDigest}}",
              "runtimeConstraints": ["linux-amd64"],
              "accelerators": [],
              "resources": { "memory": "2Gi", "cpu": "2" },
              "maxAttempts": 3,
              "leaseSeconds": 60,
              "staleTimeoutSeconds": 120,
              "executionTimeoutSeconds": 900,
              "videoPolicy": "required",
              "inputSchemaVersions": {},
              "outputSchemaVersion": "1.0.0"
            }
            """)!;
        using var created = await admin.SendAsync(
            HttpMethod.Post, "/api/v1/registry/analyst-profiles", new { profileId, imageDigest, content }, ct);
        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync(ct));
        var version = JsonDocument.Parse(await created.Content.ReadAsStringAsync(ct)).RootElement.GetProperty("version").GetInt32();

        var approval = new HttpRequestMessage(
            HttpMethod.Post, $"/api/v1/registry/analyst-profiles/{profileId}/versions/{version}/approval");
        approval.Headers.Add("X-CSRF-Token", admin.AntiforgeryToken);
        approval.Headers.TryAddWithoutValidation("If-Match", created.Headers.ETag!.Tag);
        using var approved = await admin.Client.SendAsync(approval, ct);
        approved.StatusCode.ShouldBe(HttpStatusCode.OK, await approved.Content.ReadAsStringAsync(ct));

        using var workflow = await admin.SendAsync(HttpMethod.Post, "/api/v1/registry/workflows", new
        {
            workflowId = WorkflowId,
            workflowVersion = "1",
            requestedCapabilities = new[] { ShotBoundaryAnalystImage.CapabilityId },
            completionPolicy = "all-requested-capabilities",
            segmentationPolicy = new { version = 1, durationSeconds = CutMedia.SegmentSeconds, digest = "sha-256:" + new string('1', 64) },
            nodes = new[] { new { nodeKey = NodeKey, profileId, profileVersion = version } },
            edges = Array.Empty<object>(),
        }, ct);
        workflow.StatusCode.ShouldBe(HttpStatusCode.Created, await workflow.Content.ReadAsStringAsync(ct));

        var put = new HttpRequestMessage(HttpMethod.Put, "/api/v1/registry/default-workflow")
        {
            Content = JsonContent.Create(new { workflowId = WorkflowId, workflowVersion = "1" }),
        };
        put.Headers.Add("X-CSRF-Token", admin.AntiforgeryToken);
        using var made = await admin.Client.SendAsync(put, ct);
        made.IsSuccessStatusCode.ShouldBeTrue(await made.Content.ReadAsStringAsync(ct));
    }

    private static async Task<Guid> FinalizeAndStartRunAsync(EndToEndHarness harness, CancellationToken ct)
    {
        var hierarchy = await new ClubHierarchyBuilder(harness.Platform, harness.Admin).CreateAsync(ct);
        var coachId = await TestMembers.SeedAsync(harness.Database, "coach-e2e", cancellationToken: ct);
        await FactReadEndToEndTests.ExecuteAsync(
            harness, "INSERT INTO socalytics.team_role_assignment (member_account_id, team_id, role, assigned_at, assigned_by_account_id) VALUES (@m, @t, 'coach', now(), @m)",
            [("m", coachId), ("t", hierarchy.TeamId)], ct);
        using var coach = await ApiSession.SignInAsync(harness.Platform, "coach-e2e", TestMembers.DefaultPassword, ct);

        using var media = CutMedia.Create();
        var uploaded = await media.UploadAndFinalizeAsync(coach, hierarchy.MatchId, ct);

        await using var scope = harness.Platform.Services.CreateAsyncScope();
        var lineage = (await scope.ServiceProvider.GetRequiredService<IRecordingSetLookup>().GetAsync(uploaded.RecordingSetVersionId, ct))!;
        var notification = new RecordingsFinalizedNotification(
            Guid.NewGuid(), "1.0.0", DateTimeOffset.UtcNow, lineage.RecordingSetVersionId, lineage.MatchId, lineage.TeamId,
            [.. lineage.Members.Select(m => new FinalizedNotificationMember(
                m.Position, m.RecordingVersionId, m.RecordingContentDigest, m.TimelineMappingId, m.TimelineMappingDigest))]);
        var outcome = await scope.ServiceProvider.GetRequiredService<HandleRecordingsFinalizedNotificationHandler>().HandleAsync(notification, ct);
        return outcome.ShouldBeOfType<NotificationOutcome.RunCreated>().RunId;
    }

    private static async Task WaitForAcceptedAsync(EndToEndHarness harness, Guid runId, CancellationToken ct)
    {
        try
        {
            await EndToEndHarness.WaitAsync(
                async () => await SegmentResultsAsync(harness, runId, ct) >= CutMedia.SegmentCount,
                TimeSpan.FromMinutes(5), "The accepted segment results did not arrive in time.", ct);
        }
        catch (TimeoutException e)
        {
            throw new TimeoutException(e.Message + "\n" + await WorkExecutionEndToEndTests.DiagnosticsAsync(harness, runId, ct));
        }

        (await SegmentResultsAsync(harness, runId, ct)).ShouldBe(CutMedia.SegmentCount);
    }

    private static async Task<long> SegmentResultsAsync(EndToEndHarness harness, Guid runId, CancellationToken ct)
    {
        await using var sql = new NpgsqlConnection(harness.Database.MigratorConnectionString);
        await sql.OpenAsync(ct);
        await using var command = new NpgsqlCommand(
            "SELECT count(DISTINCT j.id) FROM socalytics.analysis_accepted_results r JOIN socalytics.analysis_logical_jobs j ON j.id = r.logical_job_id " +
            "WHERE r.run_id = @runId AND j.segment_number IS NOT NULL", sql);
        command.Parameters.AddWithValue("runId", runId);
        return (long)(await command.ExecuteScalarAsync(ct))!;
    }

    private static Task<List<Guid>> AttemptIdsAsync(EndToEndHarness harness, Guid runId, CancellationToken ct) =>
        SegmentMediaEndToEndTests.AttemptIdsAsync(harness, runId, ct);
}
