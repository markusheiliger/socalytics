using System.Text.Json.Nodes;
using Dapper;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.Registry;
using SocAlytics.Platform.Domain.Recordings;
using SocAlytics.Platform.Domain.Registry;

namespace SocAlytics.Platform.Infrastructure.Persistence.Registry;

internal sealed class AnalystProfileStore(IDbSession session) : IAnalystProfileStore
{
    private const string Select =
        "SELECT profile_id AS ProfileId, profile_version AS ProfileVersion, profile_digest AS ProfileDigest, " +
        "content::text AS Content, state AS State, created_at AS CreatedAt, created_by AS CreatedBy, " +
        "approved_at AS ApprovedAt, approved_by AS ApprovedBy, retired_at AS RetiredAt, retired_by AS RetiredBy, " +
        "version AS Version FROM socalytics.analyst_profile";

    private sealed class Row
    {
        public string ProfileId { get; set; } = "";
        public int ProfileVersion { get; set; }
        public string ProfileDigest { get; set; } = "";
        public string Content { get; set; } = "";
        public string State { get; set; } = "";
        public DateTime CreatedAt { get; set; }
        public Guid CreatedBy { get; set; }
        public DateTime? ApprovedAt { get; set; }
        public Guid? ApprovedBy { get; set; }
        public DateTime? RetiredAt { get; set; }
        public Guid? RetiredBy { get; set; }
        public long Version { get; set; }
    }

    public async Task<bool> TryInsertAsync(AnalystProfile profile, CancellationToken cancellationToken)
    {
        session.RequireTransaction();
        var connection = await session.GetConnectionAsync(cancellationToken);
        var c = profile.Content;
        var rows = await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO socalytics.analyst_profile (profile_id, profile_version, profile_digest, image_digest, capability_id, " +
            "capability_version, model_digest, content, state, created_at, created_by, approved_at, approved_by, retired_at, retired_by) " +
            "VALUES (@ProfileId, @Version, @Digest, @ImageDigest, @CapabilityId, @CapabilityVersion, @ModelDigest, @Content::jsonb, " +
            "@State, @CreatedAt, @CreatedBy, @ApprovedAt, @ApprovedBy, @RetiredAt, @RetiredBy) ON CONFLICT DO NOTHING",
            new
            {
                profile.ProfileId, profile.Version, Digest = profile.Digest.ToString(), c.ImageDigest, c.CapabilityId,
                c.CapabilityVersion, c.ModelDigest, Content = c.ToCanonicalJson(), State = profile.State.ToPersistedName(),
                profile.CreatedAt, profile.CreatedBy, profile.ApprovedAt, profile.ApprovedBy, profile.RetiredAt, profile.RetiredBy,
            },
            session.Transaction,
            cancellationToken: cancellationToken));
        return rows == 1;
    }

    public async Task<StoredAnalystProfile?> FindAsync(string profileId, int version, CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(
            Select + " WHERE profile_id = @profileId AND profile_version = @version",
            new { profileId, version }, session.Transaction, cancellationToken: cancellationToken));
        return row is null ? null : Map(row);
    }

    public async Task<StoredAnalystProfile?> FindByDigestAsync(string digest, CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(
            Select + " WHERE profile_digest = @digest", new { digest }, session.Transaction, cancellationToken: cancellationToken));
        return row is null ? null : Map(row);
    }

    public async Task<IReadOnlyList<StoredAnalystProfile>> ListAsync(CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<Row>(new CommandDefinition(
            Select + " ORDER BY profile_id, profile_version", transaction: session.Transaction, cancellationToken: cancellationToken));
        return rows.Select(Map).ToList();
    }

    public Task<VersionedWriteResult> UpdateStateAsync(AnalystProfile profile, long expectedVersion, CancellationToken cancellationToken)
    {
        var transaction = session.RequireTransaction();
        var key = new { profile.ProfileId, profile.Version };
        return VersionedWrites.ExecuteAsync(
            session,
            new CommandDefinition(
                "UPDATE socalytics.analyst_profile SET state = @State, approved_at = @ApprovedAt, approved_by = @ApprovedBy, " +
                "retired_at = @RetiredAt, retired_by = @RetiredBy WHERE profile_id = @ProfileId AND profile_version = @Version " +
                "AND version = @ExpectedVersion RETURNING version",
                new
                {
                    State = profile.State.ToPersistedName(), profile.ApprovedAt, profile.ApprovedBy, profile.RetiredAt,
                    profile.RetiredBy, key.ProfileId, key.Version, ExpectedVersion = expectedVersion,
                },
                transaction,
                cancellationToken: cancellationToken),
            new CommandDefinition(
                "SELECT version FROM socalytics.analyst_profile WHERE profile_id = @ProfileId AND profile_version = @Version",
                key, transaction, cancellationToken: cancellationToken));
    }

    private static StoredAnalystProfile Map(Row row)
    {
        _ = ProfileStateExtensions.TryParse(row.State, out var state);
        var profile = new AnalystProfile(
            row.ProfileId, row.ProfileVersion, Sha256Digest.Parse(row.ProfileDigest), ParseContent(row.Content), state,
            row.CreatedBy, Utc(row.CreatedAt), row.ApprovedBy, Utc(row.ApprovedAt), row.RetiredBy, Utc(row.RetiredAt));
        return new StoredAnalystProfile(profile, row.Version);
    }

    private static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private static DateTimeOffset? Utc(DateTime? value) => value is { } v ? Utc(v) : null;

    private static AnalystProfileContent ParseContent(string json)
    {
        var n = JsonNode.Parse(json)!.AsObject();
        return new AnalystProfileContent(
            n["capabilityId"]!.GetValue<string>(),
            n["capabilityVersion"]!.GetValue<string>(),
            n["imageDigest"]!.GetValue<string>(),
            n["modelDigest"]!.GetValue<string>(),
            n["runtimeConstraints"]!.AsArray().Select(x => x!.GetValue<string>()).ToList(),
            n["accelerators"]!.AsArray().Select(x => x!.GetValue<string>()).ToList(),
            Map(n["resources"]!),
            n["maxAttempts"]!.GetValue<int>(),
            n["leaseSeconds"]!.GetValue<int>(),
            n["staleTimeoutSeconds"]!.GetValue<int>(),
            n["executionTimeoutSeconds"]!.GetValue<int>(),
            n["videoPolicy"]!.GetValue<string>(),
            Map(n["inputSchemaVersions"]!),
            n["outputSchemaVersion"]!.GetValue<string>());
    }

    private static Dictionary<string, string> Map(JsonNode node) =>
        node.AsObject().ToDictionary(p => p.Key, p => p.Value!.GetValue<string>(), StringComparer.Ordinal);
}
