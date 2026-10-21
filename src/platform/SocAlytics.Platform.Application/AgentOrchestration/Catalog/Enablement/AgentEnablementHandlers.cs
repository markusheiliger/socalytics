using System.Text.Json;
using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.IdentityAccess;
using SocAlytics.Platform.Application.Telemetry;
using SocAlytics.Platform.Domain.AgentOrchestration;
using SocAlytics.Platform.Domain.AgentOrchestration.Catalog;

namespace SocAlytics.Platform.Application.AgentOrchestration.Catalog.Enablement;

public sealed record AgentEnablementView(
    string AgentId, string Group, string? EnabledVersion, string? ChangedBy, DateTimeOffset? ChangedAt, long RowVersion);

public sealed record SetAgentEnablementCommand(string AgentId, string? Version, long ExpectedVersion);

/// <summary>Whether this stamp may enable agent versions that use the simulated model provider (Development and Test only).</summary>
public interface ICatalogEnvironment
{
    bool AllowsSimulatedProvider { get; }
}

public sealed class ListAgentEnablementsHandler(
    IUnitOfWork unitOfWork, IAccessAuthorizer authorizer, IAuditTrail audit, IAgentEnablementStore enablements)
{
    public async Task<OperationResult<IReadOnlyList<AgentEnablementView>>> HandleAsync(CancellationToken cancellationToken)
    {
        await using var scope = await unitOfWork.BeginAsync(cancellationToken);
        if (!await AgentCatalogAccess.AuthorizeAsync(
                authorizer, audit, "list-agent-enablements", new AuditResource(AgentCatalogAuditEvents.EnablementResourceType, null), cancellationToken))
        {
            return OperationFailure.Forbidden();
        }

        var records = await enablements.ListAsync(cancellationToken);
        return OperationResult<IReadOnlyList<AgentEnablementView>>.Success([.. records.Select(AgentEnablementViews.Of)]);
    }
}

public sealed class SetAgentEnablementHandler(
    IUnitOfWork unitOfWork,
    IAccessAuthorizer authorizer,
    IAuditTrail audit,
    IAgentEnablementStore enablements,
    IAgentVersionStore versions,
    IAgentCatalogArtifactStore artifacts,
    ICatalogEnvironment environment,
    IRequestContext context,
    TimeProvider time)
{
    public async Task<OperationResult<AgentEnablementView>> HandleAsync(SetAgentEnablementCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await using var scope = await unitOfWork.BeginAsync(cancellationToken);
        if (!await AgentCatalogAccess.AuthorizeAsync(
                authorizer, audit, "set-agent-enablement",
                new AuditResource(AgentCatalogAuditEvents.EnablementResourceType, command.AgentId), cancellationToken))
        {
            return OperationFailure.Forbidden();
        }

        if (context.MemberAccountId is not { } memberId)
        {
            return OperationFailure.Unauthenticated();
        }

        var current = await enablements.FindAsync(command.AgentId, false, cancellationToken);
        if (current is null)
        {
            return OperationFailure.NotFound();
        }

        if (current.Version != command.ExpectedVersion)
        {
            return OperationFailure.VersionMismatch(current.Version);
        }

        if (command.Version is not null && !SemanticVersion.TryParse(command.Version, out _))
        {
            return OperationFailure.Validation(new FieldViolation("version", "invalid-value"));
        }

        var changedAt = time.GetUtcNow();
        var write = await enablements.SetAsync(command.AgentId, command.Version, memberId.ToString(), changedAt, command.ExpectedVersion, cancellationToken);
        if (write.Outcome != VersionedWriteOutcome.Applied)
        {
            return write.Outcome == VersionedWriteOutcome.NotFound ? OperationFailure.NotFound() : OperationFailure.VersionMismatch(write.Version);
        }

        // Checked after the enablement row is locked by the update, so a concurrent retirement is either seen here or waits for this unit of work.
        if (command.Version is not null && await CheckEnableableAsync(command.AgentId, command.Version, cancellationToken) is { } refusal)
        {
            return refusal;
        }

        await audit.RecordAsync(AgentCatalogAuditEvents.EnablementChanged(command.AgentId, current.AgentVersion, command.Version), cancellationToken);
        await scope.CommitAsync(cancellationToken);
        AgentCatalogMetrics.EnablementChanged();
        return new AgentEnablementView(
            command.AgentId, AgentCatalogIds.GroupOf(command.AgentId) ?? string.Empty, command.Version, memberId.ToString(), changedAt,
            write.Version ?? current.Version + 1);
    }

    private async Task<OperationFailure?> CheckEnableableAsync(string agentId, string version, CancellationToken cancellationToken)
    {
        var stored = await versions.FindAsync(agentId, version, cancellationToken);
        if (stored is null)
        {
            return OperationFailure.Validation(new FieldViolation("version", "unknown-version"));
        }

        if (!string.Equals(stored.Group, AgentCatalogIds.GroupSpecialist, StringComparison.Ordinal))
        {
            return OperationFailure.Conflict(AgentCatalogProblems.CoachAgentEnablementUnsupported);
        }

        if (stored.State != AgentVersionState.Approved)
        {
            return NotEnableable(stored.State == AgentVersionState.Retired ? AgentCatalogProblems.ReasonRetired : AgentCatalogProblems.ReasonNotApproved);
        }

        if (!environment.AllowsSimulatedProvider && await UsesSimulatedProviderAsync(stored, cancellationToken))
        {
            return NotEnableable(AgentCatalogProblems.ReasonSimulatedProvider);
        }

        return null;
    }

    private async Task<bool> UsesSimulatedProviderAsync(AgentVersionRecord stored, CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(stored.Document);
        var model = document.RootElement.GetProperty("artifacts").GetProperty("modelSelection");
        var record = await artifacts.FindAsync(
            AgentCatalogRecordKind.ModelSelection, model.GetProperty("id").GetString()!, model.GetProperty("version").GetString()!, cancellationToken);
        if (record is null)
        {
            return false;
        }

        using var selection = JsonDocument.Parse(record.Document);
        return selection.RootElement.GetProperty("provider").GetString() == "simulated";
    }

    private static OperationFailure NotEnableable(string reason) =>
        OperationFailure.Conflict(AgentCatalogProblems.AgentVersionNotEnableable) with
        {
            Extensions = new Dictionary<string, string> { [AgentCatalogProblems.ReasonExtension] = reason },
        };
}

public static class AgentEnablementViews
{
    public static AgentEnablementView Of(AgentEnablementRecord record) =>
        new(record.AgentId, AgentCatalogIds.GroupOf(record.AgentId) ?? string.Empty, record.AgentVersion, record.ChangedBy, record.ChangedAt, record.Version);
}
