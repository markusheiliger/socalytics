using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.IdentityAccess;
using SocAlytics.Platform.Domain.AgentOrchestration;
using SocAlytics.Platform.Domain.AgentOrchestration.Catalog;

namespace SocAlytics.Platform.Application.AgentOrchestration.Catalog;

/// <summary>Documents are the stored canonical JSON texts; the API layer writes them as JSON values.</summary>
public sealed record AgentCatalogArtifactView(
    string Kind,
    string Id,
    string Version,
    string Digest,
    string RegisteredBy,
    DateTimeOffset RegisteredAt,
    string Document);

public sealed record AgentVersionView(
    string AgentId,
    string Version,
    string Digest,
    string Group,
    string State,
    bool Enabled,
    string Document,
    string RegisteredBy,
    DateTimeOffset RegisteredAt,
    string? ApprovedBy,
    DateTimeOffset? ApprovedAt,
    bool? SelfApproval,
    bool? DifferentiationAcknowledged,
    string? RetiredBy,
    DateTimeOffset? RetiredAt,
    long RowVersion);

public sealed record CatalogBaselineAgentView(string AgentId, string Group, string Domain, string DisplayName, IReadOnlyList<string> PermittedTools);

/// <summary>The catalog baseline as the <c>catalog-baseline/v1</c> document.</summary>
public sealed record CatalogBaselineView(
    string SchemaVersion,
    string Kind,
    string Version,
    IReadOnlyList<string> ToolAllowlist,
    IReadOnlyList<CatalogBaselineAgentView> Agents);

public static class AgentCatalogViews
{
    public static AgentCatalogArtifactView Of(AgentCatalogArtifactRecord record) =>
        new(AgentCatalogRecordKinds.ToName(record.Kind), record.Id, record.Version, record.Digest, record.RegisteredBy, record.RegisteredAt, record.Document);

    public static AgentVersionView Of(AgentVersionRecord record, bool enabled) =>
        new(record.AgentId, record.AgentVersion, record.Digest, record.Group, AgentVersionStates.ToPersistedName(record.State), enabled,
            record.Document, record.RegisteredBy, record.RegisteredAt, record.ApprovedBy, record.ApprovedAt, record.SelfApproval,
            record.DifferentiationAcknowledged, record.RetiredBy, record.RetiredAt, record.Version);

    public static CatalogBaselineView Baseline() =>
        new("1.0.0", "catalog-baseline", AgentCatalogIds.BaselineVersion, AgentCatalogIds.ToolAllowlist,
            [.. AgentCatalogIds.All.Select(id => new CatalogBaselineAgentView(
                id, AgentCatalogIds.GroupOf(id)!, AgentCatalogIds.DomainOf(id)!, AgentCatalogIds.DisplayNames[id], AgentCatalogIds.PermittedToolsOf(id)!))]);
}

/// <summary>Club Admin check of every catalog operation; a refusal also records the catalog's own audit event.</summary>
public static class AgentCatalogAccess
{
    public static async Task<bool> AuthorizeAsync(
        IAccessAuthorizer authorizer, IAuditTrail audit, string action, AuditResource resource, CancellationToken cancellationToken)
    {
        var decision = await authorizer.AuthorizeClubAsync(ClubPermission.Administer, resource, cancellationToken);
        if (decision.IsGranted)
        {
            return true;
        }

        await audit.RecordIndependentAsync(AgentCatalogAuditEvents.AccessRefused(action), cancellationToken);
        return false;
    }
}
