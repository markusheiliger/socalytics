using System.Text.Json;
using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Telemetry;
using SocAlytics.Platform.Domain.AgentOrchestration.Catalog;
using SocAlytics.Platform.Domain.Registry;

namespace SocAlytics.Platform.Application.AgentOrchestration.Catalog.Registration;

public sealed class RegisterCatalogResult<T>
    where T : class
{
    private RegisterCatalogResult()
    {
    }

    public T? Record { get; private init; }

    /// <summary>False when an equal record already existed and was returned unchanged.</summary>
    public bool Created { get; private init; }

    public IReadOnlyList<AgentCatalogViolation> Violations { get; private init; } = [];

    public OperationFailure? Failure { get; private init; }

    /// <summary>The profile distances reported with a registered philosophy profile.</summary>
    public IReadOnlyList<ProfileDistanceView>? Distances { get; private set; }

    public RegisterCatalogResult<T> WithDistances(IReadOnlyList<ProfileDistanceView> distances)
    {
        Distances = distances;
        return this;
    }

    public bool IsRegistered => Record is not null;

    public static RegisterCatalogResult<T> Registered(T record, bool created) => new() { Record = record, Created = created };

    public static RegisterCatalogResult<T> Invalid(IReadOnlyList<AgentCatalogViolation> violations) => new() { Violations = violations };

    public static RegisterCatalogResult<T> Failed(OperationFailure failure) => new() { Failure = failure };
}

/// <summary>
/// Validates, stores, audits, and counts a catalog registration inside the caller's unit of work. The member handlers
/// authorize before calling it; the Development and Test seed calls it as the system actor. Refusals are audited in
/// their own transaction so they persist although nothing is stored.
/// </summary>
public sealed class AgentCatalogRegistrar(
    AgentCatalogRecordValidator validator,
    IAgentCatalogArtifactStore artifacts,
    IAgentVersionStore versions,
    IAuditTrail audit,
    TimeProvider time)
{
    public async Task<RegisterCatalogResult<AgentCatalogArtifactRecord>> RegisterArtifactAsync(
        AgentCatalogRecordKind kind, JsonElement document, string registeredBy, bool system, CancellationToken cancellationToken)
    {
        var kindName = AgentCatalogRecordKinds.ToName(kind);
        var validation = await validator.ValidateArtifactAsync(kind, document, cancellationToken);
        if (!validation.IsValid)
        {
            await RefuseAsync(kindName, StringOf(document, "id"), StringOf(document, "version"), validation.Violations, cancellationToken);
            return RegisterCatalogResult<AgentCatalogArtifactRecord>.Invalid(validation.Violations);
        }

        var validated = validation.Record!;
        var record = new AgentCatalogArtifactRecord(
            kind, validated.Id, validated.Version!, SemanticVersion.MajorOf(validated.Version) ?? 0, validated.Digest, validated.Group,
            validated.CanonicalDocument, registeredBy, time.GetUtcNow());
        var inserted = await artifacts.InsertAsync(record, cancellationToken);
        if (!string.Equals(inserted.Record.Digest, validated.Digest, StringComparison.Ordinal))
        {
            await RefuseAsync(kindName, record.Id, record.Version, [], cancellationToken, AgentCatalogProblems.VersionExists);
            return RegisterCatalogResult<AgentCatalogArtifactRecord>.Failed(OperationFailure.Conflict(AgentCatalogProblems.VersionExists));
        }

        await RecordAcceptedAsync(kindName, record.Id, record.Version, record.Digest, inserted.Created, system, cancellationToken);
        return RegisterCatalogResult<AgentCatalogArtifactRecord>.Registered(inserted.Record, inserted.Created);
    }

    /// <summary>
    /// The agent-version document names no version itself, so the version is supplied beside it. The digest covers both,
    /// which keeps equal content under two versions two distinct records.
    /// </summary>
    public async Task<RegisterCatalogResult<AgentVersionRecord>> RegisterAgentVersionAsync(
        string? version, JsonElement document, string registeredBy, bool system, CancellationToken cancellationToken)
    {
        var kindName = AgentCatalogAuditEvents.AgentVersionKind;
        var agentId = StringOf(document, "agentId");
        var validation = await validator.ValidateAgentVersionAsync(document, cancellationToken);
        var violations = validation.Violations.ToList();
        if (!SemanticVersion.TryParse(version, out _))
        {
            violations.Add(new(AgentCatalogViolationCodes.SchemaViolation, "/version", "The agent version is a semantic version."));
        }

        if (violations.Count > 0 || validation.Record is null)
        {
            await RefuseAsync(kindName, agentId, Truncate(version), violations, cancellationToken);
            return RegisterCatalogResult<AgentVersionRecord>.Invalid(violations);
        }

        var validated = validation.Record;
        var digest = DigestOf(version!, validated.CanonicalDocument);
        var record = new AgentVersionRecord(
            validated.Id, version!, validated.Group!, digest, validated.CanonicalDocument, AgentVersionState.Draft, registeredBy,
            time.GetUtcNow(), null, null, null, null, null, null, 0);
        var inserted = await versions.InsertAsync(record, cancellationToken);
        if (!string.Equals(inserted.Record.Digest, digest, StringComparison.Ordinal))
        {
            await RefuseAsync(kindName, record.AgentId, version, [], cancellationToken, AgentCatalogProblems.VersionExists);
            return RegisterCatalogResult<AgentVersionRecord>.Failed(OperationFailure.Conflict(AgentCatalogProblems.VersionExists));
        }

        await RecordAcceptedAsync(kindName, record.AgentId, version!, digest, inserted.Created, system, cancellationToken);
        return RegisterCatalogResult<AgentVersionRecord>.Registered(inserted.Record, inserted.Created);
    }

    private async Task RecordAcceptedAsync(
        string kind, string id, string version, string digest, bool created, bool system, CancellationToken cancellationToken)
    {
        await audit.RecordAsync(
            created
                ? AgentCatalogAuditEvents.RecordAccepted(kind, id, version, digest, system)
                : AgentCatalogAuditEvents.RecordReplayed(kind, id, version, digest, system),
            cancellationToken);
        AgentCatalogMetrics.RegistrationRecorded(kind, created ? AgentCatalogAuditEvents.Accepted : AgentCatalogAuditEvents.Replayed);
    }

    private static string DigestOf(string version, string canonicalDocument)
    {
        using var parsed = JsonDocument.Parse($"{{\"agentVersion\":{JsonSerializer.Serialize(version)},\"record\":{canonicalDocument}}}");
        return ContentDigest.OfCanonical(CanonicalJson.Canonicalize(parsed.RootElement)).ToString();
    }

    private async Task RefuseAsync(
        string kind,
        string? id,
        string? version,
        IReadOnlyList<AgentCatalogViolation> violations,
        CancellationToken cancellationToken,
        string reason = AgentCatalogProblems.ValidationFailed)
    {
        await audit.RecordIndependentAsync(
            AgentCatalogAuditEvents.RecordRefused(kind, id, version, violations.Select(v => v.Code), reason), cancellationToken);
        AgentCatalogMetrics.RegistrationRecorded(kind, "refused");
    }

    private static string? StringOf(JsonElement document, string name) =>
        document.ValueKind == JsonValueKind.Object && document.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? Truncate(value.GetString())
            : null;

    // Refused documents are untrusted, so only a bounded identity reaches the audit trail.
    private static string? Truncate(string? value) => value is { Length: > 128 } ? value[..128] : value;
}
