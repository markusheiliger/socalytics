using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using SocAlytics.Platform.Application.Registry;
using SocAlytics.Platform.Domain.Analysis;
using SocAlytics.Platform.Domain.Registry;

namespace SocAlytics.Platform.Infrastructure.Persistence.Registry;

internal sealed class CapabilityDeclarationStore(IDbSession session) : ICapabilityDeclarationStore
{
    private const string Select =
        "SELECT image_digest AS ImageDigest, repository AS Repository, manifest_digest AS ManifestDigest, " +
        "manifest::text AS Manifest, source AS Source, registered_at AS RegisteredAt, registered_by AS RegisteredBy " +
        "FROM socalytics.analyst_capability_declaration";

    private sealed class Row
    {
        public string ImageDigest { get; set; } = "";
        public string Repository { get; set; } = "";
        public string ManifestDigest { get; set; } = "";
        public string Manifest { get; set; } = "";
        public string Source { get; set; } = "";
        public DateTime RegisteredAt { get; set; }
        public Guid? RegisteredBy { get; set; }
    }

    public async Task<bool> TryInsertAsync(StoredCapabilityDeclaration stored, CancellationToken cancellationToken)
    {
        session.RequireTransaction();
        var connection = await session.GetConnectionAsync(cancellationToken);
        var d = stored.Declaration;
        var rows = await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO socalytics.analyst_capability_declaration (image_digest, repository, manifest_digest, capability_id, " +
            "capability_version, tier, execution_scope, model_digest, manifest, source, registered_at, registered_by) VALUES " +
            "(@ImageDigest, @Repository, @ManifestDigest, @CapabilityId, @CapabilityVersion, @Tier, @Scope, @ModelDigest, " +
            "@Manifest::jsonb, @Source, @RegisteredAt, @RegisteredBy) ON CONFLICT (image_digest) DO NOTHING",
            new
            {
                d.ImageDigest, stored.Repository, stored.ManifestDigest, d.CapabilityId, d.CapabilityVersion,
                Tier = AnalysisPersistedNames.ToPersistedName(d.Tier),
                Scope = AnalysisPersistedNames.ToPersistedName(d.ExecutionScope),
                d.ModelDigest, Manifest = stored.ManifestJson, stored.Source, stored.RegisteredAt, stored.RegisteredBy,
            },
            session.Transaction,
            cancellationToken: cancellationToken));
        return rows == 1;
    }

    public async Task<StoredCapabilityDeclaration?> FindAsync(string imageDigest, CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(
            Select + " WHERE image_digest = @imageDigest", new { imageDigest }, session.Transaction, cancellationToken: cancellationToken));
        return row is null ? null : Map(row);
    }

    public async Task<IReadOnlyList<StoredCapabilityDeclaration>> ListAsync(CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<Row>(new CommandDefinition(
            Select + " ORDER BY capability_id, capability_version, image_digest", transaction: session.Transaction, cancellationToken: cancellationToken));
        return rows.Select(Map).ToList();
    }

    private static StoredCapabilityDeclaration Map(Row row) => new(
        ParseManifest(row.ImageDigest, row.Manifest), row.Repository, row.ManifestDigest, row.Manifest, row.Source,
        new DateTimeOffset(DateTime.SpecifyKind(row.RegisteredAt, DateTimeKind.Utc)), row.RegisteredBy);

    private static CapabilityDeclaration ParseManifest(string imageDigest, string json)
    {
        var root = JsonNode.Parse(json)!.AsObject();
        var capability = root["capability"]!.AsObject();
        _ = AnalysisPersistedNames.TryParseCapabilityTier(capability["tier"]!.GetValue<string>(), out var tier);
        _ = AnalysisPersistedNames.TryParseExecutionScope(capability["executionScope"]!.GetValue<string>(), out var scope);
        var runtime = root["runtime"]!.AsObject();
        var requirements = root["requirements"]!.AsObject();

        var upstream = new List<DeclaredUpstream>();
        foreach (var item in root["upstream"]?.AsArray() ?? [])
        {
            _ = AnalysisPersistedNames.TryParseDependencyKind(item!["kind"]!.GetValue<string>(), out var kind);
            var coverage = item["coverage"] is JsonObject coverageNode
                ? CoveragePolicy.Partial(coverageNode["minimumAcceptedFraction"]?.GetValue<decimal>())
                : CoveragePolicy.Complete;
            upstream.Add(new(
                item["capabilityId"]!.GetValue<string>(), item["capabilityVersion"]!.GetValue<string>(),
                item["schemaVersion"]!.GetValue<string>(), kind)
            {
                CoveragePolicy = coverage,
            });
        }

        return new CapabilityDeclaration(
            imageDigest,
            capability["id"]!.GetValue<string>(),
            capability["version"]!.GetValue<string>(),
            tier,
            scope,
            root["model"]!["digest"]!.GetValue<string>(),
            root["videoAccess"]!.GetValue<string>(),
            root["output"]!["schemaVersion"]!.GetValue<string>(),
            Strings(root["compatibleProfiles"]),
            Strings(runtime["constraints"]),
            Strings(runtime["accelerators"]),
            (root["resources"]?.AsObject() ?? []).ToDictionary(p => p.Key, p => p.Value!.GetValue<string>(), StringComparer.Ordinal),
            upstream,
            new DeclaredRequirements(
                requirements["minMaxAttempts"]!.GetValue<int>(),
                requirements["minLeaseSeconds"]!.GetValue<int>(),
                requirements["minStaleTimeoutSeconds"]!.GetValue<int>(),
                requirements["minExecutionTimeoutSeconds"]!.GetValue<int>()))
        {
            FactPayloadSchemas = PayloadSchemas(root["output"]),
        };
    }

    private static Dictionary<string, JsonElement> PayloadSchemas(JsonNode? output)
    {
        var schemas = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (output?["factPayloadSchemas"] is JsonObject declared)
        {
            foreach (var (kind, schema) in declared)
            {
                schemas[kind] = JsonSerializer.Deserialize<JsonElement>(schema!.ToJsonString());
            }
        }

        return schemas;
    }

    private static List<string> Strings(JsonNode? node) =>
        node?.AsArray().Select(n => n!.GetValue<string>()).ToList() ?? [];
}
