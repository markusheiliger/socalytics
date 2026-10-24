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

public sealed class FactPayloadImportTests(PostgresContainerFixture postgres, OciRegistryContainerFixture registry)
    : IClassFixture<OciRegistryContainerFixture>
{
    private const string Path = "/api/v1/registry/analyst-images";
    private const string Prefix = "analysts/";
    private const string ModelDigest = "sha-256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private static string Repo() => Prefix + "a" + Guid.NewGuid().ToString("N")[..10];

    private static JsonObject Schemas(string json) => JsonNode.Parse(json)!.AsObject();

    private static JsonObject Manifest(string version, JsonObject? schemas)
    {
        var node = JsonNode.Parse("""
            {
              "manifestVersion": "1.2.0",
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
        node["manifestVersion"] = version;
        if (schemas is not null)
        {
            node["output"]!["factPayloadSchemas"] = schemas;
        }

        return node;
    }

    private async Task<string> PublishAsync(JsonObject manifest)
    {
        var file = Encoding.UTF8.GetBytes(manifest.ToJsonString());
        using var publisher = new TestImagePublisher(registry.RegistryHost);
        var spec = new TestImageSpec
        {
            Repository = Repo(),
            ManifestFile = file,
            Labels = new Dictionary<string, string>
            {
                ["io.socalytics.analyst-manifest.version"] = manifest["manifestVersion"]!.GetValue<string>(),
                ["io.socalytics.analyst-manifest.sha256"] = TestImagePublisher.DigestOf(file)["sha256:".Length..],
                ["io.socalytics.capability.id"] = "player-detection",
                ["io.socalytics.capability.version"] = "1.2.0",
                ["io.socalytics.model.digest"] = ModelDigest,
            },
            ManifestPath = "opt/socalytics/analyst-manifest.json",
            FillerBytes = 4096,
        };
        var image = await publisher.PublishAsync(spec);
        return image.Reference(registry.RegistryHost);
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
            },
            omitWorkflow: true);
        await factory.WaitUntilHealthyExceptWorkflowAsync(ct);
        await TestMembers.SeedAsync(db, "admin-1", clubRoles: ["club-admin"], cancellationToken: ct);
        return (db, factory);
    }

    private static async Task<long> CountAsync(IsolatedDatabase db, string sql, CancellationToken ct)
    {
        await using var c = new NpgsqlConnection(db.AppConnectionString);
        await c.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, c);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    private static async Task<JsonElement> RefusalAsync(ApiSession session, string reference, CancellationToken ct)
    {
        using var response = await session.SendAsync(HttpMethod.Post, Path, new { reference }, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, body);
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static string DeepSchema(int levels)
    {
        var inner = "{\"type\":\"string\"}";
        for (var i = 0; i < levels; i++)
        {
            inner = "{\"type\":\"object\",\"properties\":{\"p\":" + inner + "}}";
        }

        return inner;
    }

    [Fact]
    public async Task Imports_declared_payload_schemas_unchanged_and_manifests_without_schemas_as_before()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, factory) = await StartAsync(ct);
        await using var _ = db;
        await using var __ = factory;
        using var admin = await ApiSession.SignInAsync(factory, "admin-1", TestMembers.DefaultPassword, ct);

        var schemas = Schemas("""
            {
              "player-position": {
                "type": "object",
                "required": ["x", "y"],
                "additionalProperties": false,
                "properties": {
                  "x": { "type": "number", "minimum": 0, "maximum": 105 },
                  "y": { "type": "number", "minimum": 0, "maximum": 68 },
                  "label": { "$ref": "#/$defs/label" }
                },
                "$defs": { "label": { "type": "string", "maxLength": 20 } }
              },
              "ball-contact": { "type": "object", "properties": { "player": { "type": "integer" } } }
            }
            """);
        var withSchemas = await PublishAsync(Manifest("1.2.0", schemas));
        using var created = await admin.SendAsync(HttpMethod.Post, Path, new { reference = withSchemas }, ct);
        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync(ct));
        var imageDigest = JsonDocument.Parse(await created.Content.ReadAsStringAsync(ct)).RootElement.GetProperty("imageDigest").GetString()!;

        using var get = await admin.SendAsync(HttpMethod.Get, $"{Path}/{Uri.EscapeDataString(imageDigest)}", null, ct);
        get.StatusCode.ShouldBe(HttpStatusCode.OK);
        var declared = JsonDocument.Parse(await get.Content.ReadAsStringAsync(ct)).RootElement
            .GetProperty("manifest").GetProperty("output").GetProperty("factPayloadSchemas");
        JsonNode.DeepEquals(JsonNode.Parse(declared.GetRawText()), schemas).ShouldBeTrue();
        (await CountAsync(db, "SELECT count(*) FROM socalytics.analyst_capability_declaration WHERE manifest #> '{output,factPayloadSchemas}' IS NOT NULL", ct)).ShouldBe(1);

        var without = await PublishAsync(Manifest("1.2.0", null));
        using var plain = await admin.SendAsync(HttpMethod.Post, Path, new { reference = without }, ct);
        plain.StatusCode.ShouldBe(HttpStatusCode.Created, await plain.Content.ReadAsStringAsync(ct));
        JsonDocument.Parse(await plain.Content.ReadAsStringAsync(ct)).RootElement
            .GetProperty("manifest").GetProperty("output").TryGetProperty("factPayloadSchemas", out var absent).ShouldBeFalse();

        var older = await PublishAsync(Manifest("1.0.0", null));
        using var old = await admin.SendAsync(HttpMethod.Post, Path, new { reference = older }, ct);
        old.StatusCode.ShouldBe(HttpStatusCode.Created, await old.Content.ReadAsStringAsync(ct));
        (await CountAsync(db, "SELECT count(*) FROM socalytics.analyst_capability_declaration", ct)).ShouldBe(3);
    }

    [Fact]
    public async Task Refuses_each_invalid_payload_schema_declaration_with_a_named_reason_and_caches_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, factory) = await StartAsync(ct);
        await using var _ = db;
        await using var __ = factory;
        using var admin = await ApiSession.SignInAsync(factory, "admin-1", TestMembers.DefaultPassword, ct);

        var tooMany = new JsonObject();
        for (var i = 0; i < 65; i++)
        {
            tooMany[$"kind-{i}"] = Schemas("""{ "type": "object" }""");
        }

        var oversized = Schemas("""{ "k": { "type": "object", "description": "" } }""");
        oversized["k"]!["description"] = new string('d', 17 * 1024);
        var manyNodes = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() };
        for (var i = 0; i < 260; i++)
        {
            manyNodes["properties"]![$"p{i}"] = Schemas("""{ "type": "string" }""");
        }

        var cases = new (string Name, JsonObject Schemas)[]
        {
            ("too-many-kinds", tooMany),
            ("unknown-keyword", Schemas("""{ "k": { "type": "object", "pattern": "x" } }""")),
            ("external-ref", Schemas("""{ "k": { "type": "object", "properties": { "a": { "$ref": "https://example.test/s.json" } } } }""")),
            ("unresolved-ref", Schemas("""{ "k": { "type": "object", "properties": { "a": { "$ref": "#/$defs/missing" } } } }""")),
            ("cyclic-ref", Schemas("""{ "k": { "type": "object", "properties": { "a": { "$ref": "#/$defs/a" } }, "$defs": { "a": { "type": "object", "properties": { "b": { "$ref": "#/$defs/a" } } } } } }""")),
            ("root-not-object", Schemas("""{ "k": { "type": "string" } }""")),
            ("too-large", oversized),
            ("too-deep", Schemas("{ \"k\": " + DeepSchema(9) + " }")),
            ("too-many-nodes", new JsonObject { ["k"] = manyNodes }),
            ("not-a-schema", Schemas("""{ "k": "object" }""")),
        };

        var codes = new Dictionary<string, string>();
        foreach (var (name, schemas) in cases)
        {
            var reference = await PublishAsync(Manifest("1.2.0", schemas));
            var body = await RefusalAsync(admin, reference, ct);
            body.GetProperty("code").GetString().ShouldBe("manifest-invalid", name);
            var errors = body.GetProperty("errors").EnumerateArray().ToArray();
            errors.ShouldNotBeEmpty(name);
            errors.ShouldAllBe(e => e.GetProperty("field").ValueKind == JsonValueKind.String && e.GetProperty("code").ValueKind == JsonValueKind.String, name);
            codes[name] = string.Join(",", errors.Select(e => e.GetProperty("field").GetString() + ":" + e.GetProperty("code").GetString()));
        }

        // Domain rules name their reason and the offending kind.
        codes["unresolved-ref"].ShouldBe("/output/factPayloadSchemas/k:payload-schema-unresolved-ref");
        codes["cyclic-ref"].ShouldBe("/output/factPayloadSchemas/k:payload-schema-cyclic-ref");
        codes["root-not-object"].ShouldBe("/output/factPayloadSchemas/k:payload-schema-root-not-object");
        codes["too-large"].ShouldContain("payload-schema-too-large");
        codes["too-deep"].ShouldContain("payload-schema-too-deep");
        codes["too-many-nodes"].ShouldContain("payload-schema-too-many-nodes");
        codes["too-many-kinds"].ShouldNotBeNullOrEmpty();

        // A manifest older than 1.2.0 cannot declare schemas.
        var old = await PublishAsync(Manifest("1.1.0", Schemas("""{ "k": { "type": "object" } }""")));
        (await RefusalAsync(admin, old, ct)).GetProperty("code").GetString().ShouldBe("manifest-invalid");

        (await CountAsync(db, "SELECT count(*) FROM socalytics.analyst_capability_declaration", ct)).ShouldBe(0);
        (await CountAsync(db, "SELECT count(*) FROM socalytics.security_audit_event WHERE event_type = 'registry.analyst-image.refused' AND outcome = 'refused'", ct)).ShouldBe(cases.Length + 1);
    }
}
