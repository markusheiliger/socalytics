using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.IdentityAccess;
using SocAlytics.Platform.Application.Registry.Abstractions;
using SocAlytics.Platform.Domain.Registry;

namespace SocAlytics.Platform.Application.Registry.Decisions;

internal static class RegistrationDecisionSupport
{
    public const string Expired = "analyst-manager-registration-expired";

    public static async Task<OperationResult<AnalystManagerRegistration>> DecideAsync(
        Guid registrationId,
        string? reason,
        bool reject,
        IUnitOfWork unitOfWork,
        IAccessAuthorizer authorizer,
        IRegistrationStore store,
        IAuditTrail audit,
        IRequestContext context,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        await using var scope = await unitOfWork.BeginAsync(cancellationToken);
        var decision = await authorizer.AuthorizeClubAsync(
            ClubPermission.Administer, new AuditResource(RegistryAuditEvents.ResourceType, registrationId.ToString()), cancellationToken);
        if (!decision.IsGranted)
        {
            return OperationFailure.Forbidden();
        }

        if (reject && AnalystManagerRegistration.ValidateReason(reason) is not null)
        {
            return OperationFailure.Validation(new FieldViolation("reason", "Required (1–500 chars)."));
        }

        var registration = await store.FindByIdAsync(registrationId, cancellationToken);
        if (registration is null)
        {
            return OperationFailure.NotFound();
        }

        var now = time.GetUtcNow();
        var actor = context.MemberAccountId ?? Guid.Empty;
        var result = reject ? registration.Reject(actor, reason, now) : registration.Approve(actor, now);
        if (result.Violation == RegistrationTransitionResult.RegistrationExpired)
        {
            if (registration.Expire(AnalystManagerRegistration.ApprovalWindowElapsed, now).Succeeded
                && (await store.UpdateAsync(registration, RegistrationState.PendingApproval, false, cancellationToken)).Outcome == VersionedWriteOutcome.Applied)
            {
                await audit.RecordAsync(RegistryAuditEvents.Expired(registration.Id, AnalystManagerRegistration.ApprovalWindowElapsed, new Dictionary<string, string>
                {
                    ["fromState"] = nameof(RegistrationState.PendingApproval),
                    ["toState"] = nameof(RegistrationState.Expired),
                }), cancellationToken);
                await scope.CommitAsync(cancellationToken);
            }

            return OperationFailure.Conflict(Expired);
        }

        if (!result.Succeeded)
        {
            return result.Violation == RegistrationTransitionResult.ReasonInvalid
                ? OperationFailure.Validation(new FieldViolation("reason", "Required (1–500 chars)."))
                : OperationFailure.Conflict(RegistrationTransitionResult.InvalidStateTransition);
        }

        var write = await store.UpdateAsync(registration, RegistrationState.PendingApproval, false, cancellationToken);
        if (write.Outcome != VersionedWriteOutcome.Applied)
        {
            return OperationFailure.Conflict(RegistrationTransitionResult.InvalidStateTransition);
        }

        var details = new Dictionary<string, string>
        {
            ["fromState"] = nameof(RegistrationState.PendingApproval),
            ["toState"] = registration.State.ToString(),
        };
        await audit.RecordAsync(
            reject ? RegistryAuditEvents.Rejected(registration.Id, details) : RegistryAuditEvents.Approved(registration.Id, details),
            cancellationToken);
        await scope.CommitAsync(cancellationToken);
        return registration.WithVersion(write.Version ?? registration.Version);
    }
}

public sealed class ApproveRegistrationHandler(
    IUnitOfWork unitOfWork, IAccessAuthorizer authorizer, IRegistrationStore store, IAuditTrail audit,
    IRequestContext context, TimeProvider time)
{
    public Task<OperationResult<AnalystManagerRegistration>> HandleAsync(Guid registrationId, CancellationToken cancellationToken) =>
        RegistrationDecisionSupport.DecideAsync(
            registrationId, null, false, unitOfWork, authorizer, store, audit, context, time, cancellationToken);
}
