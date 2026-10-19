using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.IdentityAccess;
using SocAlytics.Platform.Domain.AgentOrchestration.Catalog;

namespace SocAlytics.Platform.Application.AgentOrchestration.Catalog.Reads;

public sealed class GetAgentCatalogBaselineHandler(IUnitOfWork unitOfWork, IAccessAuthorizer authorizer, IAuditTrail audit)
{
    public async Task<OperationResult<CatalogBaselineView>> HandleAsync(CancellationToken cancellationToken)
    {
        await using var scope = await unitOfWork.BeginAsync(cancellationToken);
        return await AgentCatalogAccess.AuthorizeAsync(
            authorizer, audit, "read-agent-catalog-baseline", new AuditResource(AgentCatalogAuditEvents.CatalogResourceType, null), cancellationToken)
            ? AgentCatalogViews.Baseline()
            : OperationFailure.Forbidden();
    }
}

public sealed class ListAgentCatalogArtifactsHandler(
    IUnitOfWork unitOfWork, IAccessAuthorizer authorizer, IAuditTrail audit, IAgentCatalogArtifactStore artifacts)
{
    public async Task<OperationResult<IReadOnlyList<AgentCatalogArtifactView>>> HandleAsync(
        AgentCatalogRecordKind kind, string? id, CancellationToken cancellationToken)
    {
        await using var scope = await unitOfWork.BeginAsync(cancellationToken);
        if (!await AgentCatalogAccess.AuthorizeAsync(
                authorizer, audit, "list-agent-catalog-artifacts", new AuditResource(AgentCatalogAuditEvents.ArtifactResourceType, null), cancellationToken))
        {
            return OperationFailure.Forbidden();
        }

        var records = await artifacts.ListAsync(kind, string.IsNullOrEmpty(id) ? null : id, cancellationToken);
        return OperationResult<IReadOnlyList<AgentCatalogArtifactView>>.Success([.. records.Select(AgentCatalogViews.Of)]);
    }
}

public sealed class GetAgentCatalogArtifactHandler(
    IUnitOfWork unitOfWork, IAccessAuthorizer authorizer, IAuditTrail audit, IAgentCatalogArtifactStore artifacts)
{
    public async Task<OperationResult<AgentCatalogArtifactView>> HandleAsync(
        AgentCatalogRecordKind kind, string id, string version, CancellationToken cancellationToken)
    {
        await using var scope = await unitOfWork.BeginAsync(cancellationToken);
        if (!await AgentCatalogAccess.AuthorizeAsync(
                authorizer, audit, "get-agent-catalog-artifact",
                new AuditResource(AgentCatalogAuditEvents.ArtifactResourceType, $"{AgentCatalogRecordKinds.ToName(kind)}/{id}/{version}"), cancellationToken))
        {
            return OperationFailure.Forbidden();
        }

        var record = await artifacts.FindAsync(kind, id, version, cancellationToken);
        return record is null ? OperationFailure.NotFound() : AgentCatalogViews.Of(record);
    }
}

public sealed class ListAgentVersionsHandler(
    IUnitOfWork unitOfWork, IAccessAuthorizer authorizer, IAuditTrail audit, IAgentVersionStore versions, IAgentEnablementStore enablements)
{
    public async Task<OperationResult<IReadOnlyList<AgentVersionView>>> HandleAsync(
        string? agentId, string? state, CancellationToken cancellationToken)
    {
        AgentVersionState? wanted = null;
        if (!string.IsNullOrEmpty(state))
        {
            if (!AgentVersionStates.TryParse(state, out var parsed))
            {
                return OperationFailure.Validation(new FieldViolation("state", "invalid-value"));
            }

            wanted = parsed;
        }

        await using var scope = await unitOfWork.BeginAsync(cancellationToken);
        if (!await AgentCatalogAccess.AuthorizeAsync(
                authorizer, audit, "list-agent-versions", new AuditResource(AgentCatalogAuditEvents.AgentVersionResourceType, null), cancellationToken))
        {
            return OperationFailure.Forbidden();
        }

        var enabled = (await enablements.ListAsync(cancellationToken))
            .Where(e => e.AgentVersion is not null)
            .ToDictionary(e => e.AgentId, e => e.AgentVersion!, StringComparer.Ordinal);
        var records = await versions.ListAsync(string.IsNullOrEmpty(agentId) ? null : agentId, wanted, cancellationToken);
        return OperationResult<IReadOnlyList<AgentVersionView>>.Success([
            .. records.Select(r => AgentCatalogViews.Of(r, enabled.TryGetValue(r.AgentId, out var v) && v == r.AgentVersion))]);
    }
}

public sealed class GetAgentVersionHandler(
    IUnitOfWork unitOfWork, IAccessAuthorizer authorizer, IAuditTrail audit, IAgentVersionStore versions, IAgentEnablementStore enablements)
{
    public async Task<OperationResult<AgentVersionView>> HandleAsync(string agentId, string version, CancellationToken cancellationToken)
    {
        await using var scope = await unitOfWork.BeginAsync(cancellationToken);
        if (!await AgentCatalogAccess.AuthorizeAsync(
                authorizer, audit, "get-agent-version",
                new AuditResource(AgentCatalogAuditEvents.AgentVersionResourceType, $"{agentId}/{version}"), cancellationToken))
        {
            return OperationFailure.Forbidden();
        }

        var record = await versions.FindAsync(agentId, version, cancellationToken);
        if (record is null)
        {
            return OperationFailure.NotFound();
        }

        var enablement = await enablements.FindAsync(agentId, false, cancellationToken);
        return AgentCatalogViews.Of(record, enablement?.AgentVersion == version);
    }
}
