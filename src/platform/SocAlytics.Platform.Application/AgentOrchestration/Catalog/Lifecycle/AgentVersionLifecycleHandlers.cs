using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.IdentityAccess;
using SocAlytics.Platform.Application.Telemetry;
using SocAlytics.Platform.Domain.AgentOrchestration.Catalog;

namespace SocAlytics.Platform.Application.AgentOrchestration.Catalog.Lifecycle;

public sealed record AgentVersionTransitionCommand(string AgentId, string Version, long ExpectedVersion);

public sealed record ApproveAgentVersionCommand(string AgentId, string Version, long ExpectedVersion, bool DifferentiationReviewAcknowledged);

public sealed class ApproveAgentVersionHandler(
    IUnitOfWork unitOfWork,
    IAccessAuthorizer authorizer,
    IAuditTrail audit,
    IAgentVersionStore versions,
    IAgentEnablementStore enablements,
    IRequestContext context,
    TimeProvider time)
{
    public async Task<OperationResult<AgentVersionView>> HandleAsync(ApproveAgentVersionCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await using var scope = await unitOfWork.BeginAsync(cancellationToken);
        if (!await AgentCatalogAccess.AuthorizeAsync(
                authorizer, audit, "approve-agent-version",
                new AuditResource(AgentCatalogAuditEvents.AgentVersionResourceType, $"{command.AgentId}/{command.Version}"), cancellationToken))
        {
            return OperationFailure.Forbidden();
        }

        if (context.MemberAccountId is not { } memberId)
        {
            return OperationFailure.Unauthenticated();
        }

        var stored = await versions.FindAsync(command.AgentId, command.Version, cancellationToken);
        if (stored is null)
        {
            return OperationFailure.NotFound();
        }

        if (stored.State != AgentVersionState.Draft)
        {
            return OperationFailure.Conflict(AgentCatalogProblems.AgentVersionNotDraft);
        }

        if (stored.Version != command.ExpectedVersion)
        {
            return OperationFailure.VersionMismatch(stored.Version);
        }

        var approver = memberId.ToString();
        var selfApproval = string.Equals(stored.RegisteredBy, approver, StringComparison.Ordinal);
        var approval = new AgentVersionApproval(approver, time.GetUtcNow(), selfApproval, command.DifferentiationReviewAcknowledged);
        var write = await versions.ApproveAsync(command.AgentId, command.Version, approval, command.ExpectedVersion, cancellationToken);
        if (write.Outcome != VersionedWriteOutcome.Applied)
        {
            return write.Outcome == VersionedWriteOutcome.NotFound ? OperationFailure.NotFound() : OperationFailure.VersionMismatch(write.Version);
        }

        await audit.RecordAsync(
            AgentCatalogAuditEvents.VersionApproved(stored.AgentId, stored.AgentVersion, stored.Digest, selfApproval, command.DifferentiationReviewAcknowledged),
            cancellationToken);
        var approved = stored with
        {
            State = AgentVersionState.Approved,
            ApprovedBy = approval.ApprovedBy,
            ApprovedAt = approval.ApprovedAt,
            SelfApproval = selfApproval,
            DifferentiationAcknowledged = command.DifferentiationReviewAcknowledged,
            Version = write.Version ?? stored.Version,
        };
        var enablement = await enablements.FindAsync(command.AgentId, false, cancellationToken);
        await scope.CommitAsync(cancellationToken);
        AgentCatalogMetrics.ApprovalRecorded();
        return AgentCatalogViews.Of(approved, enablement?.AgentVersion == command.Version);
    }
}

public sealed class RetireAgentVersionHandler(
    IUnitOfWork unitOfWork,
    IAccessAuthorizer authorizer,
    IAuditTrail audit,
    IAgentVersionStore versions,
    IAgentEnablementStore enablements,
    IRequestContext context,
    TimeProvider time)
{
    public async Task<OperationResult<AgentVersionView>> HandleAsync(AgentVersionTransitionCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await using var scope = await unitOfWork.BeginAsync(cancellationToken);
        if (!await AgentCatalogAccess.AuthorizeAsync(
                authorizer, audit, "retire-agent-version",
                new AuditResource(AgentCatalogAuditEvents.AgentVersionResourceType, $"{command.AgentId}/{command.Version}"), cancellationToken))
        {
            return OperationFailure.Forbidden();
        }

        if (context.MemberAccountId is not { } memberId)
        {
            return OperationFailure.Unauthenticated();
        }

        var stored = await versions.FindAsync(command.AgentId, command.Version, cancellationToken);
        if (stored is null)
        {
            return OperationFailure.NotFound();
        }

        if (stored.State != AgentVersionState.Approved)
        {
            return OperationFailure.Conflict(AgentCatalogProblems.AgentVersionNotApproved);
        }

        if (stored.Version != command.ExpectedVersion)
        {
            return OperationFailure.VersionMismatch(stored.Version);
        }

        // The share lock keeps the enablement from moving onto this version while it retires.
        var enablement = await enablements.FindAsync(command.AgentId, true, cancellationToken);
        if (enablement?.AgentVersion == command.Version)
        {
            return OperationFailure.Conflict(AgentCatalogProblems.AgentVersionEnabled);
        }

        var retirement = new AgentVersionRetirement(memberId.ToString(), time.GetUtcNow());
        var write = await versions.RetireAsync(command.AgentId, command.Version, retirement, command.ExpectedVersion, cancellationToken);
        if (write.Outcome != VersionedWriteOutcome.Applied)
        {
            return write.Outcome == VersionedWriteOutcome.NotFound ? OperationFailure.NotFound() : OperationFailure.VersionMismatch(write.Version);
        }

        await audit.RecordAsync(AgentCatalogAuditEvents.VersionRetired(stored.AgentId, stored.AgentVersion, stored.Digest), cancellationToken);
        var retired = stored with
        {
            State = AgentVersionState.Retired,
            RetiredBy = retirement.RetiredBy,
            RetiredAt = retirement.RetiredAt,
            Version = write.Version ?? stored.Version,
        };
        await scope.CommitAsync(cancellationToken);
        AgentCatalogMetrics.RetirementRecorded();
        return AgentCatalogViews.Of(retired, false);
    }
}
