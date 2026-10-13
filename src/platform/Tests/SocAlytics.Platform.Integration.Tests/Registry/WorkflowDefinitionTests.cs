using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using Shouldly;
using SocAlytics.Platform.Integration.Tests.IdentityAccess.Support;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Integration.Tests.Registry.Support;
using SocAlytics.Platform.Migrator;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Registry;

public sealed class WorkflowDefinitionTests(PostgresContainerFixture postgres, OciRegistryContainerFixture registry)
    : IClassFixture<OciRegistryContainerFixture>
{
    private const string ImagesPath = "/api/v1/registry/analyst-images";
    private const string ProfilesPath = "/api/v1/registry/analyst-profiles";
    private const string WorkflowsPath = "/api/v1/registry/workflows";
    private const string DefaultPath = "/api/v1/registry/default-workflow";
    private const string Prefix = "analysts/";
    private const string ModelDigest = "sha-256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string SegmentationDigest = "sha-256:1111111111111111111111111111111111111111111111111111111111111111";

    private sealed record Profile(string Id, int Version, string Digest, string ETag);

    private async Task<string> PublishAsync(string capability, string upstreamJson, string profileId, string outputSchema, CancellationToken ct)
    {
        var manifest = JsonNode.Parse($$"""
            {
              "manifestVersion": "1.0.0",
              "capability": { "id": "{{capability}}", "version": "1.0.0", "tier": "low-level", "executionScope": "segment" },
              "upstream": {{upstreamJson}},
              "videoAccess": "optional",
              "output": { "schemaVersion": "{{outputSchema}}" },
              "compatibleProfiles": ["{{profileId}}"],
              "model": { "digest": "{{ModelDigest}}" },
              "runtime": { "constraints": ["linux-amd64"], "accelerators": ["cuda"] },
              "resources": { "memory": "8Gi" },
              "requirements": { "minMaxAttempts": 3, "minLeaseSeconds": 60, "minStaleTimeoutSeconds": 120, "minExecutionTimeoutSeconds": 900 },
              "determinism": { "deterministic": true, "idempotent": true }
            }
            """)!;
        var file = Encoding.UTF8.GetBytes(manifest.ToJsonString());
        using var publisher = new TestImagePublisher(registry.RegistryHost);
        var image = await publisher.PublishAsync(new TestImageSpec
        {
            Repository = Prefix + "w" + Guid.NewGuid().ToString("N")[..10],
            ManifestFile = file,
            Labels = new Dictionary<string, string>
            {
                ["io.socalytics.analyst-manifest.version"] = "1.0.0",
                ["io.socalytics.analyst-manifest.sha256"] = TestImagePublisher.DigestOf(file)["sha256:".Length..],
                ["io.socalytics.capability.id"] = capability,
                ["io.socalytics.capability.version"] = "1.0.0",
                ["io.socalytics.model.digest"] = ModelDigest,
            },
            ManifestPath = "opt/socalytics/analyst-manifest.json",
            FillerBytes = 4096,
        });
        return image.Reference(registry.RegistryHost);
    }

    private static JsonObject Content(string capability, string inputSchemasJson, string outputSchema) =>
        JsonNode.Parse($$"""
            {
              "capabilityId": "{{capability}}",
              "capabilityVersion": "1.0.0",
              "modelDigest": "{{ModelDigest}}",
              "runtimeConstraints": ["linux-amd64"],
              "accelerators": ["cuda"],
              "resources": { "memory": "8Gi" },
              "maxAttempts": 3,
              "leaseSeconds": 60,
              "staleTimeoutSeconds": 120,
              "executionTimeoutSeconds": 900,
              "videoPolicy": "optional",
              "inputSchemaVersions": {{inputSchemasJson}},
              "outputSchemaVersion": "{{outputSchema}}"
            }
            """)!.AsObject();

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response, CancellationToken ct) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct)).RootElement.Clone();

    private static async Task<Profile> CreateApprovedAsync(
        ApiSession admin, string imageReference, string profileId, JsonObject content, bool approve, CancellationToken ct)
    {
        using var registered = await admin.SendAsync(HttpMethod.Post, ImagesPath, new { reference = imageReference }, ct);
        registered.StatusCode.ShouldBeOneOf(HttpStatusCode.Created, HttpStatusCode.OK);
        var imageDigest = (await ReadAsync(registered, ct)).GetProperty("imageDigest").GetString();
        using var created = await admin.SendAsync(HttpMethod.Post, ProfilesPath, new { profileId, imageDigest, content }, ct);
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var body = await ReadAsync(created, ct);
        var version = body.GetProperty("version").GetInt32();
        var etag = created.Headers.ETag!.Tag;
        if (approve)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"{ProfilesPath}/{profileId}/versions/{version}/approval");
            request.Headers.Add("X-CSRF-Token", admin.AntiforgeryToken);
            request.Headers.TryAddWithoutValidation("If-Match", etag);
            using var approved = await admin.Client.SendAsync(request, ct);
            approved.StatusCode.ShouldBe(HttpStatusCode.OK);
            etag = approved.Headers.ETag!.Tag;
        }

        return new Profile(profileId, version, body.GetProperty("profileDigest").GetString()!, etag);
    }

    private static object Workflow(
        string id, string version, Profile detect, Profile track, string edgeKind = "required", string completion = "all-requested-capabilities",
        int duration = 60, string? segmentationDigest = SegmentationDigest, bool withEdge = true, string upstreamKey = "detect", string dependentKey = "track") => new
    {
        workflowId = id,
        workflowVersion = version,
        requestedCapabilities = new[] { "tracking" },
        completionPolicy = completion,
        segmentationPolicy = new { version = 1, durationSeconds = duration, digest = segmentationDigest },
        nodes = new object[]
        {
            new { nodeKey = "detect", profileId = detect.Id, profileVersion = detect.Version },
            new { nodeKey = "track", profileId = track.Id, profileVersion = track.Version },
        },
        edges = withEdge ? new object[] { new { upstreamNodeKey = upstreamKey, dependentNodeKey = dependentKey, kind = edgeKind } } : [],
    };

    private static async Task<string> CodeAsync(HttpResponseMessage response, CancellationToken ct) =>
        (await ReadAsync(response, ct)).GetProperty("code").GetString()!;

    private static async Task<long> CountAsync(IsolatedDatabase db, string sql, CancellationToken ct)
    {
        await using var c = new NpgsqlConnection(db.AppConnectionString);
        await c.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, c);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    private static Task<HttpResponseMessage> PutDefaultAsync(ApiSession session, object body, string? etag, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, DefaultPath)
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Add("X-CSRF-Token", session.AntiforgeryToken);
        if (etag is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", etag);
        }

        return session.Client.SendAsync(request, ct);
    }

    private async Task<(IsolatedDatabase Db, PlatformApiFactory Factory)> StartAsync(CancellationToken ct)
    {
        var db = await postgres.CreateDatabaseAsync(ct);
        (await MigratorHarness.RunAsync(db, TestMigrationCatalogs.Platform(), ct)).ExitCode.ShouldBe(MigratorExitCode.Success);
        var factory = new PlatformApiFactory(
            db,
            new Dictionary<string, string?>
            {
                ["ClubDisplayName"] = "Registry Club",
                ["FirstClubAdmin:AccountName"] = "registry-admin",
                ["FirstClubAdmin:InitialPassword"] = "Initial-Admin-Pass-1234",
            },
            extraSettings: new Dictionary<string, string?>
            {
                ["Registry:Images:RegistryHost"] = registry.RegistryHost,
                ["Registry:Images:RepositoryPrefix"] = Prefix,
                ["Registry:Images:UseHttp"] = "true",
            });
        await factory.WaitUntilHealthyAsync(ct);
        await TestMembers.SeedAsync(db, "admin-1", clubRoles: ["club-admin"], cancellationToken: ct);
        await TestMembers.SeedAsync(db, "reg-1", clubRoles: ["registrar"], cancellationToken: ct);
        return (db, factory);
    }

    private async Task<(Profile Detect, Profile Track, Profile Draft)> SeedProfilesAsync(ApiSession admin, CancellationToken ct)
    {
        var detectImage = await PublishAsync("player-detection", "[]", "detect-profile", "1.0.0", ct);
        var trackUpstream = """[{ "capabilityId": "player-detection", "capabilityVersion": "1.0.0", "schemaVersion": "1.0.0", "kind": "required" }]""";
        var trackImage = await PublishAsync("tracking", trackUpstream, "track-profile", "2.0.0", ct);
        var detect = await CreateApprovedAsync(admin, detectImage, "detect-profile", Content("player-detection", "{}", "1.0.0"), true, ct);
        var inputs = """{ "player-detection": "1.0.0" }""";
        var track = await CreateApprovedAsync(admin, trackImage, "track-profile", Content("tracking", inputs, "2.0.0"), true, ct);
        var draft = await CreateApprovedAsync(
            admin, trackImage, "track-profile", Content("tracking", inputs, "2.0.0").Also(c => c["leaseSeconds"] = 90), false, ct);
        return (detect, track, draft);
    }

    [Fact]
    public async Task Defines_workflows_and_changes_the_default_atomically_with_audit()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, factory) = await StartAsync(ct);
        await using var _ = db;
        await using var __ = factory;
        using var admin = await ApiSession.SignInAsync(factory, "admin-1", TestMembers.DefaultPassword, ct);
        var (detect, track, _) = await SeedProfilesAsync(admin, ct);

        using var defined = await admin.SendAsync(HttpMethod.Post, WorkflowsPath, Workflow("default", "1", detect, track), ct);
        defined.StatusCode.ShouldBe(HttpStatusCode.Created, await defined.Content.ReadAsStringAsync(ct));
        var body = await ReadAsync(defined, ct);
        body.GetProperty("definitionDigest").GetString().ShouldStartWith("sha-256:");
        body.GetProperty("definition").GetProperty("nodes").GetArrayLength().ShouldBe(2);
        body.GetProperty("definition").GetProperty("nodes")[0].GetProperty("profileDigest").GetString().ShouldBe(detect.Digest);

        using var sameVersion = await admin.SendAsync(HttpMethod.Post, WorkflowsPath, Workflow("default", "1", detect, track, duration: 90), ct);
        sameVersion.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await CodeAsync(sameVersion, ct)).ShouldBe("workflow-version-exists");
        using var second = await admin.SendAsync(HttpMethod.Post, WorkflowsPath, Workflow("default", "2", detect, track, duration: 90), ct);
        second.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await CountAsync(db, "SELECT count(*) FROM socalytics.workflow_definition", ct)).ShouldBe(2);
        (await CountAsync(db, "SELECT count(*) FROM socalytics.security_audit_event WHERE event_type = 'registry.workflow.defined'", ct)).ShouldBe(2);

        using var list = await admin.SendAsync(HttpMethod.Get, WorkflowsPath, null, ct);
        (await ReadAsync(list, ct)).GetProperty("items").GetArrayLength().ShouldBe(2);
        using var get = await admin.SendAsync(HttpMethod.Get, $"{WorkflowsPath}/default/versions/1", null, ct);
        get.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var missing = await admin.SendAsync(HttpMethod.Get, $"{WorkflowsPath}/default/versions/9", null, ct);
        missing.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        using var noDefault = await admin.SendAsync(HttpMethod.Get, DefaultPath, null, ct);
        noDefault.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        using var first = await PutDefaultAsync(admin, new { workflowId = "default", workflowVersion = "1" }, null, ct);
        first.StatusCode.ShouldBe(HttpStatusCode.OK, await first.Content.ReadAsStringAsync(ct));
        var etag = first.Headers.ETag!.Tag;
        using var current = await admin.SendAsync(HttpMethod.Get, DefaultPath, null, ct);
        current.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ReadAsync(current, ct)).GetProperty("workflowVersion").GetString().ShouldBe("1");

        using var withoutVersion = await PutDefaultAsync(admin, new { workflowId = "default", workflowVersion = "2" }, null, ct);
        withoutVersion.StatusCode.ShouldBe(HttpStatusCode.PreconditionRequired);
        using var stale = await PutDefaultAsync(admin, new { workflowId = "default", workflowVersion = "2" }, "\"99\"", ct);
        stale.StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);
        using var unknown = await PutDefaultAsync(admin, new { workflowId = "default", workflowVersion = "7" }, etag, ct);
        unknown.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await CodeAsync(unknown, ct)).ShouldBe("workflow-not-resolvable");
        using var changed = await PutDefaultAsync(admin, new { workflowId = "default", workflowVersion = "2" }, etag, ct);
        changed.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var replay = await PutDefaultAsync(admin, new { workflowId = "default", workflowVersion = "1" }, etag, ct);
        replay.StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);

        (await CountAsync(db, "SELECT count(*) FROM socalytics.security_audit_event WHERE event_type = 'registry.default-workflow.changed'", ct)).ShouldBe(2);
        (await CountAsync(db, "SELECT count(*) FROM socalytics.default_workflow WHERE workflow_version = '2'", ct)).ShouldBe(1);
    }

    [Fact]
    public async Task Refuses_invalid_workflows_with_the_specific_reason_and_stores_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, factory) = await StartAsync(ct);
        await using var _ = db;
        await using var __ = factory;
        using var admin = await ApiSession.SignInAsync(factory, "admin-1", TestMembers.DefaultPassword, ct);
        using var registrar = await ApiSession.SignInAsync(factory, "reg-1", TestMembers.DefaultPassword, ct);
        var (detect, track, draft) = await SeedProfilesAsync(admin, ct);

        async Task ExpectInvalidAsync(object workflow, string reason)
        {
            using var response = await admin.SendAsync(HttpMethod.Post, WorkflowsPath, workflow, ct);
            response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, await response.Content.ReadAsStringAsync(ct));
            var problem = await ReadAsync(response, ct);
            problem.GetProperty("code").GetString().ShouldBe("workflow-invalid");
            problem.GetProperty("errors").EnumerateArray().Any(e => e.GetProperty("code").GetString() == reason)
                .ShouldBeTrue($"{reason}: {problem}");
        }

        await ExpectInvalidAsync(Workflow("w", "1", detect, draft), "profile-not-approved");
        await ExpectInvalidAsync(Workflow("w", "1", detect, track, withEdge: false), "missing-upstream");
        await ExpectInvalidAsync(Workflow("w", "1", detect, track, edgeKind: "optional"), "missing-upstream");
        await ExpectInvalidAsync(Workflow("w", "1", detect, track, completion: "first-one"), "unknown-completion-policy");
        await ExpectInvalidAsync(Workflow("w", "1", detect, track, duration: 0), "invalid-segmentation-policy");
        await ExpectInvalidAsync(Workflow("w", "1", detect, track, segmentationDigest: "nope"), "invalid-segmentation-policy");
        await ExpectInvalidAsync(Workflow("w", "1", detect, track, upstreamKey: "track", dependentKey: "detect"), "missing-upstream");

        var cycle = new
        {
            workflowId = "w",
            workflowVersion = "1",
            requestedCapabilities = new[] { "tracking" },
            completionPolicy = "all-requested-capabilities",
            segmentationPolicy = new { version = 1, durationSeconds = 60, digest = SegmentationDigest },
            nodes = new object[]
            {
                new { nodeKey = "detect", profileId = detect.Id, profileVersion = detect.Version },
                new { nodeKey = "track", profileId = track.Id, profileVersion = track.Version },
            },
            edges = new object[]
            {
                new { upstreamNodeKey = "detect", dependentNodeKey = "track", kind = "required" },
                new { upstreamNodeKey = "track", dependentNodeKey = "detect", kind = "required" },
            },
        };
        await ExpectInvalidAsync(cycle, "cycle");

        using var unknownProfile = await admin.SendAsync(
            HttpMethod.Post, WorkflowsPath, Workflow("w", "1", detect, track with { Version = 99 }), ct);
        unknownProfile.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);

        using var noBody = await admin.SendAsync(HttpMethod.Post, WorkflowsPath, new { }, ct);
        noBody.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        using var forbidden = await registrar.SendAsync(HttpMethod.Post, WorkflowsPath, Workflow("w", "1", detect, track), ct);
        forbidden.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        (await CountAsync(db, "SELECT count(*) FROM socalytics.workflow_definition", ct)).ShouldBe(0);
        (await CountAsync(db, "SELECT count(*) FROM socalytics.security_audit_event WHERE event_type = 'registry.workflow.defined'", ct)).ShouldBe(0);

        // A workflow that became unresolvable (retired profile) cannot become the default.
        using var defined = await admin.SendAsync(HttpMethod.Post, WorkflowsPath, Workflow("w", "2", detect, track), ct);
        defined.StatusCode.ShouldBe(HttpStatusCode.Created);
        var retire = new HttpRequestMessage(HttpMethod.Post, $"{ProfilesPath}/{track.Id}/versions/{track.Version}/retirement");
        retire.Headers.Add("X-CSRF-Token", admin.AntiforgeryToken);
        retire.Headers.TryAddWithoutValidation("If-Match", track.ETag);
        using var retired = await admin.Client.SendAsync(retire, ct);
        retired.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var notResolvable = await PutDefaultAsync(admin, new { workflowId = "w", workflowVersion = "2" }, null, ct);
        notResolvable.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await CodeAsync(notResolvable, ct)).ShouldBe("workflow-not-resolvable");
    }
}

internal static class JsonObjectExtensions
{
    public static JsonObject Also(this JsonObject node, Action<JsonObject> change)
    {
        change(node);
        return node;
    }
}
