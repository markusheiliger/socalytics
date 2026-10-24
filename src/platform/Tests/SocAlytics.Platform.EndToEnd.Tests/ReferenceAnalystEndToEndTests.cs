using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using Shouldly;
using SocAlytics.Analysts.Manager.Work;
using SocAlytics.Platform.EndToEnd.Tests.Support;
using SocAlytics.Platform.Integration.Tests;
using SocAlytics.Platform.Integration.Tests.IdentityAccess.Support;
using SocAlytics.Platform.Integration.Tests.Registry.Support;
using Xunit;

namespace SocAlytics.Platform.EndToEnd.Tests;

/// <summary>
/// The SDK-built reference Analyst runs for real on the Docker runtime against materialized segment media, and its
/// facts are accepted, indexed, and readable by a team member (FR-013, FR-014, FR-018; SC-001, SC-002, SC-004).
/// </summary>
[Collection("EndToEnd")]
public sealed class ReferenceAnalystEndToEndTests
{
    private const string Prefix = "analysts/";
    private const string WorkflowId = "reference-analyst-workflow";
    private const int PolicySeconds = 5;
    private const int Segments = SyntheticMedia.DurationSeconds / PolicySeconds;
    private const int FrameRate = 25;

    [Fact]
    public Task The_reference_Analyst_turns_the_finalized_recording_into_ten_readable_frame_second_facts() =>
        RunAsync(ReferenceAnalystImage.Baseline);

    [Fact]
    public Task The_payload_validated_reference_Analyst_facts_read_back_identical_to_the_written_payloads() =>
        RunAsync(ReferenceAnalystImage.PayloadValidated);

    [Fact]
    public async Task Facts_violating_a_stricter_registered_payload_schema_fail_the_attempt_and_no_fact_is_readable()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(8));
        var ct = cts.Token;

        var registry = new OciRegistryContainerFixture();
        await registry.InitializeAsync();
        PushedAnalystImage? image = null;
        string? compatibilityPath = null;
        try
        {
            var settings = new Dictionary<string, string?>
            {
                ["Segments:Toolchain:FfmpegPath"] = SyntheticMedia.FfmpegPath,
                ["Segments:Toolchain:FfprobePath"] = Path.Combine(Path.GetDirectoryName(SyntheticMedia.FfmpegPath) ?? string.Empty, "ffprobe"),
                ["Registry:Images:RegistryHost"] = registry.RegistryHost,
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
            image = await TestAnalystImage.BuildAndPushAsync(registry.RegistryHost, Prefix, ct, "payload-invalid");
            await DockerRuntimeEndToEndTests.DefineDefaultWorkflowAsync(
                harness, image.Reference(registry.RegistryHost), ct, "payload-invalid", maxAttempts: 1, videoPolicy: "required", segmentationSeconds: PolicySeconds);

            using var docker = new DockerEngine();
            compatibilityPath = await DockerRuntimeEndToEndTests.WriteCompatibilityListAsync(docker, ct);
            await harness.StartManagerAsync(ct, dockerCompatibilityListPath: compatibilityPath);

            var runId = await SegmentMediaEndToEndTests.FinalizeAndStartRunAsync(harness, ct);
            try
            {
                await EndToEndHarness.WaitAsync(
                    async () => await ScalarAsync(harness, runId,
                        "SELECT count(*) FROM socalytics.analysis_completion_outcomes o JOIN socalytics.analysis_logical_jobs j ON j.id = o.logical_job_id " +
                        "WHERE j.run_id = @runId AND o.outcome = 'failed' AND o.reason LIKE 'result-payload-invalid%'", ct) > 0,
                    TimeSpan.FromMinutes(3), "No attempt was failed with result-payload-invalid.", ct);
            }
            catch (TimeoutException e)
            {
                throw new TimeoutException(e.Message + "\n" + await WorkExecutionEndToEndTests.DiagnosticsAsync(harness, runId, ct));
            }

            (await ScalarAsync(harness, runId, "SELECT count(*) FROM socalytics.analysis_accepted_results WHERE run_id = @runId", ct)).ShouldBe(0);
            (await ScalarAsync(harness, runId, "SELECT count(*) FROM socalytics.analysis_indexed_facts WHERE run_id = @runId", ct))
                .ShouldBe(0, "No fact of a rejected result is readable.");

            using var coach = await ApiSession.SignInAsync(harness.Platform, "coach-e2e", TestMembers.DefaultPassword, ct);
            using var streams = await coach.Client.GetAsync($"/api/v1/analysis/runs/{runId}/fact-streams", ct);
            streams.StatusCode.ShouldBe(HttpStatusCode.OK, await streams.Content.ReadAsStringAsync(ct));
            (await streams.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("items").EnumerateArray()
                .ShouldAllBe(s => s.GetProperty("factCount").GetInt64() == 0, "A stream may be listed, but it exposes no fact.");
        }
        finally
        {
            if (image is not null)
            {
                await TestAnalystImage.RemoveAsync(image);
            }

            if (compatibilityPath is not null)
            {
                File.Delete(compatibilityPath);
            }

            await registry.DisposeAsync();
        }
    }

    private static async Task<long> ScalarAsync(EndToEndHarness harness, Guid runId, string query, CancellationToken ct)
    {
        await using var sql = new NpgsqlConnection(harness.Database.MigratorConnectionString);
        await sql.OpenAsync(ct);
        await using var command = new NpgsqlCommand(query, sql);
        command.Parameters.AddWithValue("runId", runId);
        return (long)(await command.ExecuteScalarAsync(ct))!;
    }

    private static async Task RunAsync(ReferenceAnalystVariant variant)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(12));
        var ct = cts.Token;

        var registry = new OciRegistryContainerFixture();
        await registry.InitializeAsync();
        PushedAnalystImage? image = null;
        string? compatibilityPath = null;
        try
        {
            var settings = new Dictionary<string, string?>
            {
                ["Segments:Toolchain:FfmpegPath"] = SyntheticMedia.FfmpegPath,
                ["Segments:Toolchain:FfprobePath"] = Path.Combine(Path.GetDirectoryName(SyntheticMedia.FfmpegPath) ?? string.Empty, "ffprobe"),
                ["Registry:Images:RegistryHost"] = registry.RegistryHost,
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
            image = await ReferenceAnalystImage.BuildAndPushAsync(registry.RegistryHost, Prefix, ct, variant);
            await DefineDefaultWorkflowAsync(harness, image.Reference(registry.RegistryHost), variant.CapabilityVersion, ct);

            using var docker = new DockerEngine();
            compatibilityPath = await DockerRuntimeEndToEndTests.WriteCompatibilityListAsync(docker, ct);
            await harness.StartManagerAsync(ct, dockerCompatibilityListPath: compatibilityPath);
            var managerLabel = $"io.socalytics.manager={harness.Control.GetStatus().ManagerId!}";

            var runId = await SegmentMediaEndToEndTests.FinalizeAndStartRunAsync(harness, ct);
            await SegmentMediaEndToEndTests.WaitForAcceptedAsync(harness, runId, ct);

            using var coach = await ApiSession.SignInAsync(harness.Platform, "coach-e2e", TestMembers.DefaultPassword, ct);
            var facts = await AssertFactsAsync(coach, runId, variant.CapabilityVersion, ct);
            if (variant == ReferenceAnalystImage.PayloadValidated)
            {
                await AssertWrittenPayloadsAsync(harness, runId, facts, ct);
            }

            await AssertAttemptNumbersAsync(harness, runId, ct);

            var attempts = await SegmentMediaEndToEndTests.AttemptIdsAsync(harness, runId, ct);
            await EndToEndHarness.WaitAsync(
                async () => (await docker.ListContainersAsync(managerLabel, ct)).Count == 0
                    && attempts.All(a => !Directory.Exists(UpstreamStager.DirectoryFor(a))),
                TimeSpan.FromSeconds(30), "A container, attempt directory, or staged media of an attempt remains.", ct);
        }
        finally
        {
            if (image is not null)
            {
                await ReferenceAnalystImage.RemoveAsync(image);
            }

            if (compatibilityPath is not null)
            {
                File.Delete(compatibilityPath);
            }

            await registry.DisposeAsync();
        }
    }

    private static async Task<List<JsonElement>> AssertFactsAsync(ApiSession member, Guid runId, string capabilityVersion, CancellationToken ct)
    {
        using var streamsResponse = await member.Client.GetAsync($"/api/v1/analysis/runs/{runId}/fact-streams", ct);
        streamsResponse.StatusCode.ShouldBe(HttpStatusCode.OK, await streamsResponse.Content.ReadAsStringAsync(ct));
        var stream = (await streamsResponse.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("items").EnumerateArray().Single();
        stream.GetProperty("capability").GetProperty("id").GetString().ShouldBe(ReferenceAnalystImage.CapabilityId);
        stream.GetProperty("capability").GetProperty("version").GetString().ShouldBe(capabilityVersion);
        stream.GetProperty("schemaVersion").GetString().ShouldBe("1.0.0");
        stream.GetProperty("executionScope").GetString().ShouldBe("segment");
        stream.GetProperty("factCount").GetInt64().ShouldBe(SyntheticMedia.DurationSeconds);
        var coverage = stream.GetProperty("coverage");
        coverage.GetProperty("accepted").GetInt32().ShouldBe(Segments);
        foreach (var other in new[] { "missing", "failed", "blocked", "skipped" })
        {
            coverage.GetProperty(other).GetInt32().ShouldBe(0);
        }

        var path = $"/api/v1/analysis/runs/{runId}/facts?capabilityId={ReferenceAnalystImage.CapabilityId}" +
            $"&capabilityVersion={capabilityVersion}&schemaVersion=1.0.0&pageSize=50";
        var facts = new List<JsonElement>();
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
                var windows = page.GetProperty("coverage").EnumerateArray().ToList();
                windows.ShouldNotBeEmpty();
                windows.ShouldAllBe(w => w.GetProperty("state").GetString() == "accepted");
                windows[0].GetProperty("startMs").GetInt64().ShouldBe(0);
                windows[^1].GetProperty("endMs").GetInt64().ShouldBe(SyntheticMedia.DurationSeconds * 1000L);
            }

            facts.AddRange(page.GetProperty("items").EnumerateArray());
            token = page.TryGetProperty("continuationToken", out var next) && next.ValueKind == JsonValueKind.String ? next.GetString() : null;
            pages++.ShouldBeLessThan(10);
        }
        while (token is not null);

        facts.Count.ShouldBe(SyntheticMedia.DurationSeconds);
        for (var k = 0; k < facts.Count; k++)
        {
            var fact = facts[k];
            fact.GetProperty("kind").GetString().ShouldBe("frame-second-probe");
            fact.GetProperty("startMs").GetInt64().ShouldBe(k * 1000L);
            fact.GetProperty("endMs").GetInt64().ShouldBe((k + 1) * 1000L);

            var payload = fact.GetProperty("payload");
            payload.GetProperty("decodedFrames").GetInt32().ShouldBe(FrameRate);
            var luma = payload.GetProperty("meanLuma");
            var (min, mean, max) = (luma.GetProperty("min").GetDouble(), luma.GetProperty("mean").GetDouble(), luma.GetProperty("max").GetDouble());
            min.ShouldBeGreaterThanOrEqualTo(0);
            max.ShouldBeLessThanOrEqualTo(255);
            mean.ShouldBeInRange(min, max);
        }

        return facts;
    }

    /// <summary>Reads the facts artifact of every accepted result from the object store and compares its payloads with the facts read through the API.</summary>
    private static async Task AssertWrittenPayloadsAsync(EndToEndHarness harness, Guid runId, List<JsonElement> readFacts, CancellationToken ct)
    {
        var references = new List<string>();
        await using (var sql = new NpgsqlConnection(harness.Database.MigratorConnectionString))
        {
            await sql.OpenAsync(ct);
            await using var command = new NpgsqlCommand(
                "SELECT manifest_reference FROM socalytics.analysis_accepted_results WHERE run_id = @runId", sql);
            command.Parameters.AddWithValue("runId", runId);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                references.Add(reader.GetString(0));
            }
        }

        references.Count.ShouldBe(Segments);
        using var s3 = harness.Store.CreateS3Client();
        var written = new Dictionary<long, JsonNode?>();
        foreach (var reference in references)
        {
            using var response = await s3.GetObjectAsync(harness.Store.Bucket, reference[..(reference.LastIndexOf('/') + 1)] + "facts.json", ct);
            var document = await JsonNode.ParseAsync(response.ResponseStream, cancellationToken: ct);
            foreach (var fact in document!["facts"]!.AsArray())
            {
                written.Add(fact!["startMs"]!.GetValue<long>(), fact["payload"]?.DeepClone());
            }
        }

        written.Count.ShouldBe(readFacts.Count);
        foreach (var fact in readFacts)
        {
            var expected = written[fact.GetProperty("startMs").GetInt64()];
            var actual = JsonNode.Parse(fact.GetProperty("payload").GetRawText());
            JsonNode.DeepEquals(actual, expected).ShouldBeTrue($"Payload read back: {actual}; written: {expected}.");
        }
    }

    private static async Task AssertAttemptNumbersAsync(EndToEndHarness harness, Guid runId, CancellationToken ct)
    {
        await using var sql = new NpgsqlConnection(harness.Database.MigratorConnectionString);
        await sql.OpenAsync(ct);
        await using var command = new NpgsqlCommand(
            "SELECT a.attempt_number FROM socalytics.analysis_accepted_results r JOIN socalytics.analysis_execution_attempts a ON a.id = r.attempt_id WHERE r.run_id = @runId", sql);
        command.Parameters.AddWithValue("runId", runId);
        var numbers = new List<int>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            numbers.Add(reader.GetInt32(0));
        }

        numbers.ShouldBe([1, 1]);
    }

    private static async Task DefineDefaultWorkflowAsync(EndToEndHarness harness, string reference, string capabilityVersion, CancellationToken ct)
    {
        var admin = harness.Admin;
        const string profileId = ReferenceAnalystImage.ProfileId;
        using var registered = await admin.SendAsync(HttpMethod.Post, "/api/v1/registry/analyst-images", new { reference }, ct);
        registered.StatusCode.ShouldBeOneOf(HttpStatusCode.Created, HttpStatusCode.OK);
        var imageDigest = (await Read(registered, ct)).GetProperty("imageDigest").GetString();

        var content = JsonNode.Parse($$"""
            {
              "capabilityId": "{{ReferenceAnalystImage.CapabilityId}}",
              "capabilityVersion": "{{capabilityVersion}}",
              "modelDigest": "{{ReferenceAnalystImage.ModelDigest}}",
              "runtimeConstraints": ["linux-amd64"],
              "accelerators": [],
              "resources": { "memory": "512Mi", "cpu": "1" },
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
        var version = (await Read(created, ct)).GetProperty("version").GetInt32();

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
            requestedCapabilities = new[] { ReferenceAnalystImage.CapabilityId },
            completionPolicy = "all-requested-capabilities",
            segmentationPolicy = new { version = 1, durationSeconds = PolicySeconds, digest = "sha-256:" + new string('1', 64) },
            nodes = new[] { new { nodeKey = "probe", profileId, profileVersion = version } },
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

    private static async Task<JsonElement> Read(HttpResponseMessage response, CancellationToken ct) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct)).RootElement.Clone();
}
