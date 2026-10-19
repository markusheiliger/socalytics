using System.Net;
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

public sealed class AnalystImageImportTests(PostgresContainerFixture postgres, OciRegistryContainerFixture registry)
    : IClassFixture<OciRegistryContainerFixture>
{
    private const string Path = "/api/v1/registry/analyst-images";
    private const string Prefix = "analysts/";
    private const string ModelDigest = "sha-256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private static string Repo() => Prefix + "a" + Guid.NewGuid().ToString("N")[..10];

    private static JsonObject Manifest(Action<JsonObject>? change = null)
    {
        var node = JsonNode.Parse("""
            {
              "manifestVersion": "1.0.0",
              "capability": { "id": "player-detection", "version": "1.2.0", "tier": "low-level", "executionScope": "segment" },
              "upstream": [],
              "videoAccess": "required",
              "output": { "schemaVersion": "1.0.0" },
              "compatibleProfiles": ["gpu-standard"],
              "model": { "digest": "sha-256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef" },
              "runtime": { "constraints": ["linux-amd64"], "accelerators": ["cuda"] },
              "resources": { "memory": "8Gi", "cpu": "4" },
              "requirements": { "minMaxAttempts": 3, "minLeaseSeconds": 60, "minStaleTimeoutSeconds": 120, "minExecutionTimeoutSeconds": 900 },
              "determinism": { "deterministic": true, "idempotent": true }
            }
            """)!.AsObject();
        change?.Invoke(node);
        return node;
    }

    private static Dictionary<string, string> LabelsFor(byte[] file, string capabilityId = "player-detection", string modelDigest = ModelDigest) => new()
    {
        ["io.socalytics.analyst-manifest.version"] = "1.0.0",
        ["io.socalytics.analyst-manifest.sha256"] = TestImagePublisher.DigestOf(file)["sha256:".Length..],
        ["io.socalytics.capability.id"] = capabilityId,
        ["io.socalytics.capability.version"] = "1.2.0",
        ["io.socalytics.model.digest"] = modelDigest,
    };

    private async Task<(string Reference, string Digest)> PublishAsync(
        JsonObject manifest, Func<byte[], Dictionary<string, string>>? labels = null, string? manifestPath = null)
    {
        var file = Encoding.UTF8.GetBytes(manifest.ToJsonString());
        using var publisher = new TestImagePublisher(registry.RegistryHost);
        var spec = new TestImageSpec
        {
            Repository = Repo(),
            ManifestFile = file,
            Labels = labels?.Invoke(file) ?? LabelsFor(file),
            ManifestPath = manifestPath ?? "opt/socalytics/analyst-manifest.json",
            FillerBytes = 4096,
        };
        var image = await publisher.PublishAsync(spec);
        return (image.Reference(registry.RegistryHost), image.ManifestDigest);
    }

    private async Task<(IsolatedDatabase Db, PlatformApiFactory Factory)> StartAsync(string host, CancellationToken ct)
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
                ["Registry:Images:RegistryHost"] = host,
                ["Registry:Images:RepositoryPrefix"] = Prefix,
                ["Registry:Images:UseHttp"] = "true",
            },
            omitWorkflow: true);
        await factory.WaitUntilHealthyExceptWorkflowAsync(ct);
        await TestMembers.SeedAsync(db, "admin-1", clubRoles: ["club-admin"], cancellationToken: ct);
        await TestMembers.SeedAsync(db, "reg-1", clubRoles: ["registrar"], cancellationToken: ct);
        return (db, factory);
    }

    private static async Task<long> CountAsync(IsolatedDatabase db, string sql, CancellationToken ct)
    {
        await using var c = new NpgsqlConnection(db.AppConnectionString);
        await c.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, c);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    private static async Task<string> RefusalAsync(ApiSession session, string reference, HttpStatusCode expected, CancellationToken ct)
    {
        using var response = await session.SendAsync(HttpMethod.Post, Path, new { reference }, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        response.StatusCode.ShouldBe(expected, body);
        return JsonDocument.Parse(body).RootElement.GetProperty("code").GetString()!;
    }

    private static async Task<JsonElement> RefusalBodyAsync(ApiSession session, string reference, HttpStatusCode expected, CancellationToken ct)
    {
        using var response = await session.SendAsync(HttpMethod.Post, Path, new { reference }, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        response.StatusCode.ShouldBe(expected, body);
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static string[] PropertyNames(JsonElement element) =>
        element.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray();

    private static void ShouldHaveImageShape(JsonElement image, string digest)
    {
        PropertyNames(image).ShouldBe(
            ["capability", "imageDigest", "manifest", "manifestDigest", "modelDigest", "registeredAt", "registeredBy", "repository", "source"]);
        image.GetProperty("imageDigest").GetString().ShouldBe(digest);
        image.GetProperty("repository").ValueKind.ShouldBe(JsonValueKind.String);
        image.GetProperty("manifestDigest").ValueKind.ShouldBe(JsonValueKind.String);
        image.GetProperty("modelDigest").GetString().ShouldBe(ModelDigest);
        image.GetProperty("source").ValueKind.ShouldBe(JsonValueKind.String);
        image.GetProperty("registeredAt").ValueKind.ShouldBe(JsonValueKind.String);
        image.GetProperty("registeredBy").ValueKind.ShouldBeOneOf(JsonValueKind.String, JsonValueKind.Null);

        var capability = image.GetProperty("capability");
        PropertyNames(capability).ShouldBe(["executionScope", "id", "tier", "version"]);
        capability.GetProperty("id").GetString().ShouldBe("player-detection");
        capability.GetProperty("version").GetString().ShouldBe("1.2.0");
        capability.GetProperty("tier").GetString().ShouldBe("low-level");
        capability.GetProperty("executionScope").GetString().ShouldBe("segment");

        // The manifest is the stored object, not a string or a wrapper.
        var manifest = image.GetProperty("manifest");
        manifest.ValueKind.ShouldBe(JsonValueKind.Object);
        manifest.GetProperty("manifestVersion").GetString().ShouldBe("1.0.0");
        manifest.GetProperty("capability").GetProperty("id").GetString().ShouldBe("player-detection");
        manifest.GetProperty("upstream").ValueKind.ShouldBe(JsonValueKind.Array);
    }

    [Fact]
    public async Task Registers_returns_idempotently_lists_and_reads_without_the_image_registry()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, factory) = await StartAsync(registry.RegistryHost, ct);
        await using var _ = db;
        await using var __ = factory;
        using var admin = await ApiSession.SignInAsync(factory, "admin-1", TestMembers.DefaultPassword, ct);
        var (reference, digest) = await PublishAsync(Manifest());

        using var created = await admin.SendAsync(HttpMethod.Post, Path, new { reference }, ct);
        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync(ct));
        var root = JsonDocument.Parse(await created.Content.ReadAsStringAsync(ct)).RootElement;
        root.GetProperty("imageDigest").GetString().ShouldBe(digest);
        root.GetProperty("capability").GetProperty("id").GetString().ShouldBe("player-detection");
        root.GetProperty("capability").GetProperty("tier").GetString().ShouldBe("low-level");
        root.GetProperty("modelDigest").GetString().ShouldBe(ModelDigest);
        root.GetProperty("manifest").GetProperty("upstream").GetArrayLength().ShouldBe(0);
        root.TryGetProperty("registeredAt", out var registeredAt).ShouldBeTrue();
        registeredAt.ValueKind.ShouldBe(JsonValueKind.String);
        ShouldHaveImageShape(root, digest);
        created.Headers.Location!.OriginalString.ShouldBe($"{Path}/{Uri.EscapeDataString(digest)}");

        using var again = await admin.SendAsync(HttpMethod.Post, Path, new { reference }, ct);
        again.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonDocument.Parse(await again.Content.ReadAsStringAsync(ct)).RootElement.GetProperty("registeredAt").GetString()
            .ShouldBe(root.GetProperty("registeredAt").GetString());
        (await CountAsync(db, "SELECT count(*) FROM socalytics.analyst_capability_declaration", ct)).ShouldBe(1);
        (await CountAsync(db, "SELECT count(*) FROM socalytics.security_audit_event WHERE event_type = 'registry.analyst-image.registered'", ct)).ShouldBe(1);

        using var list = await admin.SendAsync(HttpMethod.Get, Path, null, ct);
        list.StatusCode.ShouldBe(HttpStatusCode.OK);
        var items = JsonDocument.Parse(await list.Content.ReadAsStringAsync(ct)).RootElement.GetProperty("items");
        items.GetArrayLength().ShouldBe(1);
        PropertyNames(items[0]).ShouldBe(["capabilityId", "capabilityVersion", "imageDigest", "registeredAt"]);
        items[0].GetProperty("imageDigest").GetString().ShouldBe(digest);
        items[0].GetProperty("capabilityId").GetString().ShouldBe("player-detection");
        items[0].GetProperty("capabilityVersion").GetString().ShouldBe("1.2.0");
        items[0].GetProperty("registeredAt").GetString().ShouldNotBeNull();
        PropertyNames(JsonDocument.Parse(await list.Content.ReadAsStringAsync(ct)).RootElement).ShouldBe(["items"]);
        using var filtered = await admin.SendAsync(HttpMethod.Get, Path + "?capabilityId=other", null, ct);
        JsonDocument.Parse(await filtered.Content.ReadAsStringAsync(ct)).RootElement.GetProperty("items").GetArrayLength().ShouldBe(0);
        using var get = await admin.SendAsync(HttpMethod.Get, $"{Path}/{Uri.EscapeDataString(digest)}", null, ct);
        get.StatusCode.ShouldBe(HttpStatusCode.OK);
        ShouldHaveImageShape(JsonDocument.Parse(await get.Content.ReadAsStringAsync(ct)).RootElement, digest);
        using var missing = await admin.SendAsync(HttpMethod.Get, $"{Path}/sha256:{new string('0', 64)}", null, ct);
        missing.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Refuses_invalid_registrations_stores_nothing_and_audits_each_refusal()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, factory) = await StartAsync(registry.RegistryHost, ct);
        await using var _ = db;
        await using var __ = factory;
        using var admin = await ApiSession.SignInAsync(factory, "admin-1", TestMembers.DefaultPassword, ct);

        var (valid, _) = await PublishAsync(Manifest());
        var tagged = valid[..valid.IndexOf('@')] + ":latest";
        (await RefusalAsync(admin, tagged, HttpStatusCode.BadRequest, ct)).ShouldBe("reference-not-digest");
        (await RefusalAsync(admin, $"other.example/{Repo()}@sha256:{new string('a', 64)}", HttpStatusCode.BadRequest, ct)).ShouldBe("reference-not-allowed");
        (await RefusalAsync(admin, $"{registry.RegistryHost}/{Repo()}@sha256:{new string('a', 64)}", HttpStatusCode.NotFound, ct)).ShouldBe("image-not-found");

        var (noManifest, _) = await PublishAsync(Manifest(), manifestPath: "opt/other.json");
        (await RefusalAsync(admin, noManifest, HttpStatusCode.UnprocessableEntity, ct)).ShouldBe("manifest-missing");

        var (unknownTier, _) = await PublishAsync(Manifest(m => m["capability"]!["tier"] = "mid-level"));
        (await RefusalAsync(admin, unknownTier, HttpStatusCode.UnprocessableEntity, ct)).ShouldBe("manifest-invalid");
        var (extra, _) = await PublishAsync(Manifest(m => m["unknown"] = 1));
        (await RefusalAsync(admin, extra, HttpStatusCode.UnprocessableEntity, ct)).ShouldBe("manifest-invalid");

        // Duplicate upstream capabilities are refused with the other manifest problems.
        var upstream = (string id) => new JsonObject
        {
            ["capabilityId"] = id, ["capabilityVersion"] = "1.0.0", ["schemaVersion"] = "1.0.0", ["kind"] = "required",
        };
        var (duplicate, _) = await PublishAsync(Manifest(m => m["upstream"] = new JsonArray(upstream("pose"), upstream("pose"))));
        (await RefusalAsync(admin, duplicate, HttpStatusCode.UnprocessableEntity, ct)).ShouldBe("manifest-invalid");

        var (wrongId, _) = await PublishAsync(Manifest(), file => LabelsFor(file, capabilityId: "other"));
        (await RefusalAsync(admin, wrongId, HttpStatusCode.UnprocessableEntity, ct)).ShouldBe("labels-mismatch");
        var (wrongModel, _) = await PublishAsync(Manifest(), file => LabelsFor(file, modelDigest: "sha-256:" + new string('b', 64)));
        (await RefusalAsync(admin, wrongModel, HttpStatusCode.UnprocessableEntity, ct)).ShouldBe("labels-mismatch");
        var (noLabels, _) = await PublishAsync(Manifest(), _ => new Dictionary<string, string>());
        (await RefusalAsync(admin, noLabels, HttpStatusCode.UnprocessableEntity, ct)).ShouldBe("labels-mismatch");

        // Refusals keep their status, code, and errors[] of field and code.
        var mismatch = await RefusalBodyAsync(admin, wrongId, HttpStatusCode.UnprocessableEntity, ct);
        mismatch.GetProperty("code").GetString().ShouldBe("labels-mismatch");
        var mismatchErrors = mismatch.GetProperty("errors").EnumerateArray().ToArray();
        mismatchErrors.Length.ShouldBe(1);
        PropertyNames(mismatchErrors[0]).ShouldBe(["code", "field"]);
        mismatchErrors[0].GetProperty("field").GetString().ShouldBe("io.socalytics.capability.id");
        mismatchErrors[0].GetProperty("code").GetString().ShouldBe("mismatch");
        var missingLabels = await RefusalBodyAsync(admin, noLabels, HttpStatusCode.UnprocessableEntity, ct);
        missingLabels.GetProperty("errors").EnumerateArray().ShouldAllBe(e => e.GetProperty("code").GetString() == "missing");
        var invalidManifest = await RefusalBodyAsync(admin, unknownTier, HttpStatusCode.UnprocessableEntity, ct);
        invalidManifest.GetProperty("code").GetString().ShouldBe("manifest-invalid");
        var manifestErrors = invalidManifest.GetProperty("errors").EnumerateArray().ToArray();
        manifestErrors.ShouldNotBeEmpty();
        manifestErrors.ShouldAllBe(e => e.GetProperty("field").ValueKind == JsonValueKind.String && e.GetProperty("code").ValueKind == JsonValueKind.String);
        (await RefusalBodyAsync(admin, noManifest, HttpStatusCode.UnprocessableEntity, ct)).GetProperty("code").GetString().ShouldBe("manifest-missing");
        var (wrongHash, _) = await PublishAsync(Manifest(), file =>
        {
            var labels = LabelsFor(file);
            labels["io.socalytics.analyst-manifest.sha256"] = new string('c', 64);
            return labels;
        });
        (await RefusalAsync(admin, wrongHash, HttpStatusCode.UnprocessableEntity, ct)).ShouldBe("labels-mismatch");

        using var emptyBody = await admin.SendAsync(HttpMethod.Post, Path, new { reference = "" }, ct);
        emptyBody.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        (await CountAsync(db, "SELECT count(*) FROM socalytics.analyst_capability_declaration", ct)).ShouldBe(0);
        (await CountAsync(db, "SELECT count(*) FROM socalytics.security_audit_event WHERE event_type = 'registry.analyst-image.refused' AND outcome = 'refused' AND reason_code IS NOT NULL", ct)).ShouldBe(15);
    }

    private static JsonObject CoverageManifest(string version, JsonObject? coverage, string scope = "match", string kind = "required") =>
        Manifest(m =>
        {
            m["manifestVersion"] = version;
            m["capability"]!["id"] = "pressing-analysis";
            m["capability"]!["executionScope"] = scope;
            var entry = new JsonObject
            {
                ["capabilityId"] = "player-detection", ["capabilityVersion"] = "1.2.0", ["schemaVersion"] = "1.0.0", ["kind"] = kind,
            };
            if (coverage is not null)
            {
                entry["coverage"] = coverage;
            }

            m["upstream"] = new JsonArray(entry);
        });

    private static Func<byte[], Dictionary<string, string>> VersionedLabels(string version) => file =>
    {
        var labels = LabelsFor(file, capabilityId: "pressing-analysis");
        labels["io.socalytics.analyst-manifest.version"] = version;
        return labels;
    };

    private static JsonObject Partial(decimal? fraction) =>
        fraction is null
            ? new JsonObject { ["mode"] = "partial" }
            : new JsonObject { ["mode"] = "partial", ["minimumAcceptedFraction"] = fraction };

    [Fact]
    public async Task Stores_partial_coverage_declarations_of_1_1_0_manifests_and_complete_coverage_for_1_0_0()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, factory) = await StartAsync(registry.RegistryHost, ct);
        await using var _ = db;
        await using var __ = factory;
        using var admin = await ApiSession.SignInAsync(factory, "admin-1", TestMembers.DefaultPassword, ct);

        var (partial, _) = await PublishAsync(CoverageManifest("1.1.0", Partial(null)), VersionedLabels("1.1.0"));
        var (fraction, _) = await PublishAsync(CoverageManifest("1.1.0", Partial(0.8m)), VersionedLabels("1.1.0"));
        var (complete, _) = await PublishAsync(CoverageManifest("1.0.0", null), VersionedLabels("1.0.0"));

        var coverages = new List<JsonElement?>();
        foreach (var reference in new[] { partial, fraction, complete })
        {
            using var response = await admin.SendAsync(HttpMethod.Post, Path, new { reference }, ct);
            response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(ct));
            var entry = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct)).RootElement
                .GetProperty("manifest").GetProperty("upstream")[0].Clone();
            coverages.Add(entry.TryGetProperty("coverage", out var coverage) ? coverage : null);
        }

        coverages[0]!.Value.GetProperty("mode").GetString().ShouldBe("partial");
        coverages[0]!.Value.TryGetProperty("minimumAcceptedFraction", out var absent).ShouldBeFalse();
        coverages[1]!.Value.GetProperty("mode").GetString().ShouldBe("partial");
        coverages[1]!.Value.GetProperty("minimumAcceptedFraction").GetDecimal().ShouldBe(0.8m);
        coverages[2].ShouldBeNull();
        (await CountAsync(db, "SELECT count(*) FROM socalytics.analyst_capability_declaration WHERE manifest #> '{upstream,0,coverage}' IS NOT NULL", ct)).ShouldBe(2);
        (await CountAsync(db, "SELECT count(*) FROM socalytics.analyst_capability_declaration WHERE manifest #> '{upstream,0,coverage}' IS NULL", ct)).ShouldBe(1);
    }

    [Fact]
    public async Task Refuses_invalid_partial_coverage_declarations_and_stores_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, factory) = await StartAsync(registry.RegistryHost, ct);
        await using var _ = db;
        await using var __ = factory;
        using var admin = await ApiSession.SignInAsync(factory, "admin-1", TestMembers.DefaultPassword, ct);

        var invalid = new[]
        {
            CoverageManifest("1.1.0", Partial(null), scope: "segment"),
            CoverageManifest("1.1.0", Partial(null), kind: "optional"),
            CoverageManifest("1.1.0", Partial(0m)),
            CoverageManifest("1.1.0", Partial(1.5m)),
        };
        foreach (var manifest in invalid)
        {
            var (reference, _) = await PublishAsync(manifest, VersionedLabels("1.1.0"));
            (await RefusalAsync(admin, reference, HttpStatusCode.UnprocessableEntity, ct)).ShouldBe("manifest-invalid");
        }

        (await CountAsync(db, "SELECT count(*) FROM socalytics.analyst_capability_declaration", ct)).ShouldBe(0);
        (await CountAsync(db, "SELECT count(*) FROM socalytics.security_audit_event WHERE event_type = 'registry.analyst-image.refused' AND reason_code = 'manifest-invalid'", ct)).ShouldBe(4);
    }

    [Fact]
    public async Task Registry_failure_is_retryable_and_stores_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, factory) = await StartAsync("127.0.0.1:1", ct);
        await using var _ = db;
        await using var __ = factory;
        using var admin = await ApiSession.SignInAsync(factory, "admin-1", TestMembers.DefaultPassword, ct);

        var code = await RefusalAsync(admin, $"127.0.0.1:1/{Repo()}@sha256:{new string('a', 64)}", HttpStatusCode.ServiceUnavailable, ct);

        code.ShouldBe("registry-unavailable");
        (await CountAsync(db, "SELECT count(*) FROM socalytics.analyst_capability_declaration", ct)).ShouldBe(0);
    }

    [Fact]
    public async Task Members_without_club_admin_authority_are_refused_and_audited()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, factory) = await StartAsync(registry.RegistryHost, ct);
        await using var _ = db;
        await using var __ = factory;
        using var registrar = await ApiSession.SignInAsync(factory, "reg-1", TestMembers.DefaultPassword, ct);
        var (reference, digest) = await PublishAsync(Manifest());

        using var register = await registrar.SendAsync(HttpMethod.Post, Path, new { reference }, ct);
        register.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        using var list = await registrar.SendAsync(HttpMethod.Get, Path, null, ct);
        list.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        using var get = await registrar.SendAsync(HttpMethod.Get, $"{Path}/{Uri.EscapeDataString(digest)}", null, ct);
        get.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        (await CountAsync(db, "SELECT count(*) FROM socalytics.security_audit_event WHERE event_type = 'authorization.denied' AND resource_type = 'analyst-image'", ct)).ShouldBe(3);
        (await CountAsync(db, "SELECT count(*) FROM socalytics.analyst_capability_declaration", ct)).ShouldBe(0);
    }
}
