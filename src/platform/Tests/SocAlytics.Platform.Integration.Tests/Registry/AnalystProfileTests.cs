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

public sealed class AnalystProfileTests(PostgresContainerFixture postgres, OciRegistryContainerFixture registry)
    : IClassFixture<OciRegistryContainerFixture>
{
    private const string ImagesPath = "/api/v1/registry/analyst-images";
    private const string ProfilesPath = "/api/v1/registry/analyst-profiles";
    private const string Prefix = "analysts/";
    private const string ModelDigest = "sha-256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private static string VersionPath(string profileId, int version) => $"{ProfilesPath}/{profileId}/versions/{version}";

    private static JsonObject Content(Action<JsonObject>? change = null)
    {
        var node = JsonNode.Parse($$"""
            {
              "capabilityId": "player-detection",
              "capabilityVersion": "1.2.0",
              "modelDigest": "{{ModelDigest}}",
              "runtimeConstraints": ["linux-amd64"],
              "accelerators": ["cuda"],
              "resources": { "memory": "8Gi", "cpu": "4" },
              "maxAttempts": 3,
              "leaseSeconds": 60,
              "staleTimeoutSeconds": 120,
              "executionTimeoutSeconds": 900,
              "videoPolicy": "required",
              "inputSchemaVersions": {},
              "outputSchemaVersion": "1.0.0"
            }
            """)!.AsObject();
        change?.Invoke(node);
        return node;
    }

    private async Task<(IsolatedDatabase Db, PlatformApiFactory Factory, string ImageDigest)> StartAsync(CancellationToken ct)
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
            },
            omitWorkflow: true);
        await factory.WaitUntilHealthyExceptWorkflowAsync(ct);
        await TestMembers.SeedAsync(db, "admin-1", clubRoles: ["club-admin"], cancellationToken: ct);
        await TestMembers.SeedAsync(db, "admin-2", clubRoles: ["club-admin"], cancellationToken: ct);
        await TestMembers.SeedAsync(db, "reg-1", clubRoles: ["registrar"], cancellationToken: ct);

        var manifest = JsonNode.Parse($$"""
            {
              "manifestVersion": "1.0.0",
              "capability": { "id": "player-detection", "version": "1.2.0", "tier": "low-level", "executionScope": "segment" },
              "upstream": [],
              "videoAccess": "required",
              "output": { "schemaVersion": "1.0.0" },
              "compatibleProfiles": ["gpu-standard"],
              "model": { "digest": "{{ModelDigest}}" },
              "runtime": { "constraints": ["linux-amd64"], "accelerators": ["cuda"] },
              "resources": { "memory": "8Gi", "cpu": "4" },
              "requirements": { "minMaxAttempts": 3, "minLeaseSeconds": 60, "minStaleTimeoutSeconds": 120, "minExecutionTimeoutSeconds": 900 },
              "determinism": { "deterministic": true, "idempotent": true }
            }
            """)!;
        var file = Encoding.UTF8.GetBytes(manifest.ToJsonString());
        using var publisher = new TestImagePublisher(registry.RegistryHost);
        var image = await publisher.PublishAsync(new TestImageSpec
        {
            Repository = Prefix + "p" + Guid.NewGuid().ToString("N")[..10],
            ManifestFile = file,
            Labels = new Dictionary<string, string>
            {
                ["io.socalytics.analyst-manifest.version"] = "1.0.0",
                ["io.socalytics.analyst-manifest.sha256"] = TestImagePublisher.DigestOf(file)["sha256:".Length..],
                ["io.socalytics.capability.id"] = "player-detection",
                ["io.socalytics.capability.version"] = "1.2.0",
                ["io.socalytics.model.digest"] = ModelDigest,
            },
            ManifestPath = "opt/socalytics/analyst-manifest.json",
            FillerBytes = 4096,
        });
        using var admin = await ApiSession.SignInAsync(factory, "admin-1", TestMembers.DefaultPassword, ct);
        using var registered = await admin.SendAsync(HttpMethod.Post, ImagesPath, new { reference = image.Reference(registry.RegistryHost) }, ct);
        registered.StatusCode.ShouldBe(HttpStatusCode.Created, await registered.Content.ReadAsStringAsync(ct));
        return (db, factory, image.ManifestDigest);
    }

    private static Task<HttpResponseMessage> PostWithVersionAsync(ApiSession session, string path, string? etag, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.Add("X-CSRF-Token", session.AntiforgeryToken);
        if (etag is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", etag);
        }

        return session.Client.SendAsync(request, ct);
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body, string? ETag)> CreateAsync(
        ApiSession session, string imageDigest, JsonObject content, CancellationToken ct, string profileId = "gpu-standard")
    {
        using var response = await session.SendAsync(HttpMethod.Post, ProfilesPath, new { profileId, imageDigest, content }, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        text.StartsWith('{').ShouldBeTrue($"{(int)response.StatusCode}: {text}");
        return (response.StatusCode, JsonDocument.Parse(text).RootElement.Clone(), response.Headers.ETag?.Tag);
    }

    private static async Task<string> CodeAsync(HttpResponseMessage response, CancellationToken ct) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct)).RootElement.GetProperty("code").GetString()!;

    private static async Task<long> CountAsync(IsolatedDatabase db, string sql, CancellationToken ct)
    {
        await using var c = new NpgsqlConnection(db.AppConnectionString);
        await c.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, c);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    [Fact]
    public async Task Creates_draft_profiles_idempotently_with_next_versions_and_refuses_contradictions()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, factory, image) = await StartAsync(ct);
        await using var _ = db;
        await using var __ = factory;
        using var admin = await ApiSession.SignInAsync(factory, "admin-1", TestMembers.DefaultPassword, ct);

        var first = await CreateAsync(admin, image, Content(), ct);
        first.Status.ShouldBe(HttpStatusCode.Created, first.Body.ToString());
        first.Body.GetProperty("state").GetString().ShouldBe("draft");
        first.Body.GetProperty("version").GetInt32().ShouldBe(1);
        first.Body.GetProperty("imageDigest").GetString().ShouldBe(image);
        var digest = first.Body.GetProperty("profileDigest").GetString()!;
        digest.ShouldStartWith("sha-256:");
        first.ETag.ShouldNotBeNull();

        var again = await CreateAsync(admin, image, Content(), ct);
        again.Status.ShouldBe(HttpStatusCode.OK);
        again.Body.GetProperty("profileDigest").GetString().ShouldBe(digest);
        again.Body.GetProperty("version").GetInt32().ShouldBe(1);

        var second = await CreateAsync(admin, image, Content(c => c["leaseSeconds"] = 120), ct);
        second.Status.ShouldBe(HttpStatusCode.Created);
        second.Body.GetProperty("version").GetInt32().ShouldBe(2);
        second.Body.GetProperty("profileDigest").GetString().ShouldNotBe(digest);
        (await CountAsync(db, "SELECT count(*) FROM socalytics.analyst_profile", ct)).ShouldBe(2);
        (await CountAsync(db, "SELECT count(*) FROM socalytics.security_audit_event WHERE event_type = 'registry.analyst-profile.created'", ct)).ShouldBe(2);

        var lowTimeout = await CreateAsync(admin, image, Content(c => c["executionTimeoutSeconds"] = 10), ct);
        lowTimeout.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        lowTimeout.Body.GetProperty("code").GetString().ShouldBe("profile-contradicts-declaration");
        lowTimeout.Body.GetProperty("errors").EnumerateArray()
            .Any(e => e.GetProperty("field").GetString() == "content.executionTimeoutSeconds").ShouldBeTrue();
        var wrongModel = await CreateAsync(admin, image, Content(c => c["modelDigest"] = "sha-256:" + new string('b', 64)), ct);
        wrongModel.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        wrongModel.Body.GetProperty("code").GetString().ShouldBe("profile-contradicts-declaration");
        var wrongProfileId = await CreateAsync(admin, image, Content(), ct, profileId: "other");
        wrongProfileId.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var unknownImage = await CreateAsync(admin, "sha256:" + new string('0', 64), Content(), ct);
        unknownImage.Status.ShouldBe(HttpStatusCode.NotFound);
        unknownImage.Body.GetProperty("code").GetString().ShouldBe("image-not-registered");
        (await CountAsync(db, "SELECT count(*) FROM socalytics.analyst_profile", ct)).ShouldBe(2);

        using var list = await admin.SendAsync(HttpMethod.Get, ProfilesPath + "?state=draft&capabilityId=player-detection", null, ct);
        JsonDocument.Parse(await list.Content.ReadAsStringAsync(ct)).RootElement.GetProperty("items").GetArrayLength().ShouldBe(2);
        using var none = await admin.SendAsync(HttpMethod.Get, ProfilesPath + "?state=approved", null, ct);
        JsonDocument.Parse(await none.Content.ReadAsStringAsync(ct)).RootElement.GetProperty("items").GetArrayLength().ShouldBe(0);
        using var get = await admin.SendAsync(HttpMethod.Get, VersionPath("gpu-standard", 1), null, ct);
        get.StatusCode.ShouldBe(HttpStatusCode.OK);
        get.Headers.ETag.ShouldNotBeNull();
        using var missing = await admin.SendAsync(HttpMethod.Get, VersionPath("gpu-standard", 9), null, ct);
        missing.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    private static string[] Names(JsonElement element) =>
        element.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray();

    private static string[] Sorted(params string[] names) => names.Order(StringComparer.Ordinal).ToArray();

    private static void AssertProfileShape(JsonElement profile)
    {
        Names(profile).ShouldBe(Sorted(
            "profileId", "version", "profileDigest", "imageDigest", "state", "content",
            "createdAt", "createdBy", "approvedAt", "approvedBy", "retiredAt", "retiredBy"));
        var content = profile.GetProperty("content");
        Names(content).ShouldBe(Sorted(
            "capabilityId", "capabilityVersion", "modelDigest", "runtimeConstraints", "accelerators", "resources",
            "maxAttempts", "leaseSeconds", "staleTimeoutSeconds", "executionTimeoutSeconds", "videoPolicy",
            "inputSchemaVersions", "outputSchemaVersion"));
        content.GetProperty("runtimeConstraints").ValueKind.ShouldBe(JsonValueKind.Array);
        content.GetProperty("accelerators").ValueKind.ShouldBe(JsonValueKind.Array);
        content.GetProperty("resources").ValueKind.ShouldBe(JsonValueKind.Object);
        content.GetProperty("inputSchemaVersions").ValueKind.ShouldBe(JsonValueKind.Object);
        profile.GetProperty("version").ValueKind.ShouldBe(JsonValueKind.Number);
    }

    [Fact]
    public async Task Keeps_the_profile_wire_shape_headers_and_statuses_unchanged()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, factory, image) = await StartAsync(ct);
        await using var _ = db;
        await using var __ = factory;
        using var admin = await ApiSession.SignInAsync(factory, "admin-1", TestMembers.DefaultPassword, ct);

        using var created = await admin.SendAsync(HttpMethod.Post, ProfilesPath, new { profileId = "gpu-standard", imageDigest = image, content = Content() }, ct);
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        created.Headers.ETag!.Tag.ShouldBe("\"1\"");
        created.Headers.Location!.ToString().ShouldEndWith(VersionPath("gpu-standard", 1));
        var body = JsonDocument.Parse(await created.Content.ReadAsStringAsync(ct)).RootElement.Clone();
        AssertProfileShape(body);
        body.GetProperty("profileId").GetString().ShouldBe("gpu-standard");
        body.GetProperty("state").GetString().ShouldBe("draft");
        body.GetProperty("content").GetProperty("resources").GetProperty("memory").GetString().ShouldBe("8Gi");
        body.GetProperty("approvedAt").ValueKind.ShouldBe(JsonValueKind.Null);
        body.GetProperty("retiredBy").ValueKind.ShouldBe(JsonValueKind.Null);

        using var replay = await admin.SendAsync(HttpMethod.Post, ProfilesPath, new { profileId = "gpu-standard", imageDigest = image, content = Content() }, ct);
        replay.StatusCode.ShouldBe(HttpStatusCode.OK);
        replay.Headers.ETag!.Tag.ShouldBe("\"1\"");
        replay.Headers.Location!.ToString().ShouldEndWith(VersionPath("gpu-standard", 1));
        AssertProfileShape(JsonDocument.Parse(await replay.Content.ReadAsStringAsync(ct)).RootElement);

        using var read = await admin.SendAsync(HttpMethod.Get, VersionPath("gpu-standard", 1), null, ct);
        read.StatusCode.ShouldBe(HttpStatusCode.OK);
        read.Headers.ETag!.Tag.ShouldBe("\"1\"");
        AssertProfileShape(JsonDocument.Parse(await read.Content.ReadAsStringAsync(ct)).RootElement);

        using var list = await admin.SendAsync(HttpMethod.Get, ProfilesPath, null, ct);
        list.StatusCode.ShouldBe(HttpStatusCode.OK);
        var listBody = JsonDocument.Parse(await list.Content.ReadAsStringAsync(ct)).RootElement;
        Names(listBody).ShouldBe(["items"]);
        listBody.GetProperty("items").GetArrayLength().ShouldBe(1);
        AssertProfileShape(listBody.GetProperty("items")[0]);

        using var approved = await PostWithVersionAsync(admin, VersionPath("gpu-standard", 1) + "/approval", created.Headers.ETag.Tag, ct);
        approved.StatusCode.ShouldBe(HttpStatusCode.OK);
        approved.Headers.ETag.ShouldNotBeNull();
        var approvedBody = JsonDocument.Parse(await approved.Content.ReadAsStringAsync(ct)).RootElement.Clone();
        AssertProfileShape(approvedBody);
        approvedBody.GetProperty("state").GetString().ShouldBe("approved");
        approvedBody.GetProperty("approvedAt").ValueKind.ShouldBe(JsonValueKind.String);
        approvedBody.GetProperty("approvedBy").ValueKind.ShouldBe(JsonValueKind.String);

        using var retired = await PostWithVersionAsync(admin, VersionPath("gpu-standard", 1) + "/retirement", approved.Headers.ETag.Tag, ct);
        retired.StatusCode.ShouldBe(HttpStatusCode.OK);
        retired.Headers.ETag.ShouldNotBeNull();
        var retiredBody = JsonDocument.Parse(await retired.Content.ReadAsStringAsync(ct)).RootElement;
        AssertProfileShape(retiredBody);
        retiredBody.GetProperty("state").GetString().ShouldBe("retired");
        retiredBody.GetProperty("retiredAt").ValueKind.ShouldBe(JsonValueKind.String);
        retiredBody.GetProperty("retiredBy").ValueKind.ShouldBe(JsonValueKind.String);
    }

    [Fact]
    public async Task Approves_with_self_approval_marked_refuses_repeats_and_requires_the_version()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, factory, image) = await StartAsync(ct);
        await using var _ = db;
        await using var __ = factory;
        using var admin = await ApiSession.SignInAsync(factory, "admin-1", TestMembers.DefaultPassword, ct);
        using var other = await ApiSession.SignInAsync(factory, "admin-2", TestMembers.DefaultPassword, ct);
        var first = await CreateAsync(admin, image, Content(), ct);
        var second = await CreateAsync(admin, image, Content(c => c["leaseSeconds"] = 120), ct);
        var approval1 = VersionPath("gpu-standard", 1) + "/approval";

        using var missing = await PostWithVersionAsync(admin, approval1, null, ct);
        missing.StatusCode.ShouldBe(HttpStatusCode.PreconditionRequired);
        using var stale = await PostWithVersionAsync(admin, approval1, "\"99\"", ct);
        stale.StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);

        using var approved = await PostWithVersionAsync(admin, approval1, first.ETag, ct);
        approved.StatusCode.ShouldBe(HttpStatusCode.OK, await approved.Content.ReadAsStringAsync(ct));
        JsonDocument.Parse(await approved.Content.ReadAsStringAsync(ct)).RootElement.GetProperty("state").GetString().ShouldBe("approved");

        using var repeated = await PostWithVersionAsync(admin, approval1, approved.Headers.ETag!.Tag, ct);
        repeated.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await CodeAsync(repeated, ct)).ShouldBe("profile-not-draft");

        using var foreign = await PostWithVersionAsync(other, VersionPath("gpu-standard", 2) + "/approval", second.ETag, ct);
        foreign.StatusCode.ShouldBe(HttpStatusCode.OK);

        (await CountAsync(db, "SELECT count(*) FROM socalytics.security_audit_event WHERE event_type = 'registry.analyst-profile.approved' AND details->>'selfApproval' = 'true'", ct)).ShouldBe(1);
        (await CountAsync(db, "SELECT count(*) FROM socalytics.security_audit_event WHERE event_type = 'registry.analyst-profile.approved' AND details->>'selfApproval' = 'false'", ct)).ShouldBe(1);

        // An approved profile's content is immutable: only a new version can carry a change.
        var changed = await CreateAsync(admin, image, Content(c => c["maxAttempts"] = 5), ct);
        changed.Status.ShouldBe(HttpStatusCode.Created);
        changed.Body.GetProperty("version").GetInt32().ShouldBe(3);
        (await CountAsync(db, "SELECT count(*) FROM socalytics.analyst_profile WHERE state = 'approved'", ct)).ShouldBe(2);
    }

    [Fact]
    public async Task Retires_profiles_audits_and_refuses_while_the_default_workflow_uses_them()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, factory, image) = await StartAsync(ct);
        await using var _ = db;
        await using var __ = factory;
        using var admin = await ApiSession.SignInAsync(factory, "admin-1", TestMembers.DefaultPassword, ct);
        var first = await CreateAsync(admin, image, Content(), ct);
        var second = await CreateAsync(admin, image, Content(c => c["leaseSeconds"] = 120), ct);
        using var approved = await PostWithVersionAsync(admin, VersionPath("gpu-standard", 1) + "/approval", first.ETag, ct);
        approved.StatusCode.ShouldBe(HttpStatusCode.OK);
        var approvedTag = approved.Headers.ETag!.Tag;
        var digest = first.Body.GetProperty("profileDigest").GetString()!;

        await using (var c = new NpgsqlConnection(db.AppConnectionString))
        {
            await c.OpenAsync(ct);
            var definition = new JsonObject
            {
                ["nodes"] = new JsonArray(new JsonObject { ["nodeKey"] = "detect", ["profileId"] = "gpu-standard", ["profileVersion"] = 1, ["profileDigest"] = digest }),
            }.ToJsonString();
            await using var cmd = new NpgsqlCommand(
                "INSERT INTO socalytics.workflow_definition (workflow_id, workflow_version, definition_digest, definition, created_at) " +
                "VALUES ('w', '1', @d, @j::jsonb, now()); " +
                "INSERT INTO socalytics.default_workflow (workflow_id, workflow_version, changed_at) VALUES ('w', '1', now())", c);
            cmd.Parameters.AddWithValue("d", "sha256:" + new string('1', 64));
            cmd.Parameters.AddWithValue("j", definition);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        var retirement1 = VersionPath("gpu-standard", 1) + "/retirement";
        using var inUse = await PostWithVersionAsync(admin, retirement1, approvedTag, ct);
        inUse.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await CodeAsync(inUse, ct)).ShouldBe("profile-in-default-workflow");

        var retirement2 = VersionPath("gpu-standard", 2) + "/retirement";
        using var noVersion = await PostWithVersionAsync(admin, retirement2, null, ct);
        noVersion.StatusCode.ShouldBe(HttpStatusCode.PreconditionRequired);
        using var stale = await PostWithVersionAsync(admin, retirement2, "\"99\"", ct);
        stale.StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);
        using var retired = await PostWithVersionAsync(admin, retirement2, second.ETag, ct);
        retired.StatusCode.ShouldBe(HttpStatusCode.OK, await retired.Content.ReadAsStringAsync(ct));
        JsonDocument.Parse(await retired.Content.ReadAsStringAsync(ct)).RootElement.GetProperty("state").GetString().ShouldBe("retired");
        using var again = await PostWithVersionAsync(admin, retirement2, retired.Headers.ETag!.Tag, ct);
        again.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await CodeAsync(again, ct)).ShouldBe("profile-already-retired");
        (await CountAsync(db, "SELECT count(*) FROM socalytics.security_audit_event WHERE event_type = 'registry.analyst-profile.retired'", ct)).ShouldBe(1);
    }

    [Fact]
    public async Task Members_without_club_admin_authority_are_refused_and_audited()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, factory, image) = await StartAsync(ct);
        await using var _ = db;
        await using var __ = factory;
        using var admin = await ApiSession.SignInAsync(factory, "admin-1", TestMembers.DefaultPassword, ct);
        using var registrar = await ApiSession.SignInAsync(factory, "reg-1", TestMembers.DefaultPassword, ct);
        var draft = await CreateAsync(admin, image, Content(), ct);

        var create = await CreateAsync(registrar, image, Content(c => c["leaseSeconds"] = 120), ct);
        create.Status.ShouldBe(HttpStatusCode.Forbidden);
        using var approve = await PostWithVersionAsync(registrar, VersionPath("gpu-standard", 1) + "/approval", draft.ETag, ct);
        approve.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        using var retire = await PostWithVersionAsync(registrar, VersionPath("gpu-standard", 1) + "/retirement", draft.ETag, ct);
        retire.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        (await CountAsync(db, "SELECT count(*) FROM socalytics.security_audit_event WHERE event_type = 'authorization.denied' AND resource_type = 'analyst-profile'", ct)).ShouldBe(3);
        (await CountAsync(db, "SELECT count(*) FROM socalytics.analyst_profile WHERE state = 'draft'", ct)).ShouldBe(1);
    }

    [Fact]
    public async Task Concurrent_approvals_of_one_draft_succeed_exactly_once()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, factory, image) = await StartAsync(ct);
        await using var _ = db;
        await using var __ = factory;
        using var admin = await ApiSession.SignInAsync(factory, "admin-1", TestMembers.DefaultPassword, ct);
        using var other = await ApiSession.SignInAsync(factory, "admin-2", TestMembers.DefaultPassword, ct);
        var draft = await CreateAsync(admin, image, Content(), ct);
        var path = VersionPath("gpu-standard", 1) + "/approval";

        var responses = await Task.WhenAll(
            PostWithVersionAsync(admin, path, draft.ETag, ct),
            PostWithVersionAsync(other, path, draft.ETag, ct));
        try
        {
            responses.Count(r => r.StatusCode == HttpStatusCode.OK).ShouldBe(1);
            responses.Single(r => r.StatusCode != HttpStatusCode.OK).StatusCode
                .ShouldBeOneOf(HttpStatusCode.Conflict, HttpStatusCode.PreconditionFailed);
            (await CountAsync(db, "SELECT count(*) FROM socalytics.security_audit_event WHERE event_type = 'registry.analyst-profile.approved'", ct)).ShouldBe(1);
            (await CountAsync(db, "SELECT count(*) FROM socalytics.analyst_profile WHERE state = 'approved'", ct)).ShouldBe(1);
        }
        finally
        {
            foreach (var r in responses)
            {
                r.Dispose();
            }
        }
    }
}
