using Microsoft.Extensions.Options;
using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.Registry.Abstractions;
using SocAlytics.Platform.Domain.Registry;

namespace SocAlytics.Platform.Application.Registry.Pairing;

public sealed record PairingStatusResult(
    string Status, double PollingIntervalSeconds, DateTimeOffset? ExpiresAt, string? Challenge = null, DateTimeOffset? ChallengeExpiresAt = null);

public sealed class GetPairingStatusHandler(
    IUnitOfWork unitOfWork,
    IRegistrationStore store,
    IRegistrySecretGenerator secrets,
    IAuditTrail audit,
    IOptions<RegistryOptions> registryOptions,
    TimeProvider time)
{
    public const string PollingHandleInvalid = "analyst-manager-polling-handle-invalid";

    public async Task<OperationResult<PairingStatusResult>> HandleAsync(string? pollingHandle, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(pollingHandle))
        {
            return OperationFailure.Validation(PollingHandleInvalid);
        }

        var interval = registryOptions.Value.Pairing.PollingInterval.TotalSeconds;
        var now = time.GetUtcNow();
        await using var scope = await unitOfWork.BeginAsync(cancellationToken);
        var registration = await store.FindByPollingHandleHashAsync(secrets.Hash(pollingHandle), cancellationToken);
        if (registration is null)
        {
            return OperationFailure.Validation(PollingHandleInvalid);
        }

        var expected = registration.State;
        switch (expected)
        {
            case RegistrationState.Pairing when now >= registration.PairingExpiresAt:
                return await ExpireAsync(registration, expected, AnalystManagerRegistration.PairingWindowElapsed, now, scope, cancellationToken);
            case RegistrationState.Pairing:
                return new PairingStatusResult("awaiting_submission", interval, registration.PairingExpiresAt);
            case RegistrationState.PendingApproval when now >= registration.ApprovalExpiresAt:
                return await ExpireAsync(registration, expected, AnalystManagerRegistration.ApprovalWindowElapsed, now, scope, cancellationToken);
            case RegistrationState.PendingApproval:
                return new PairingStatusResult("pending_approval", interval, registration.ApprovalExpiresAt);
            case RegistrationState.Approved when now >= registration.ActivationExpiresAt:
                return await ExpireAsync(registration, expected, AnalystManagerRegistration.ActivationWindowElapsed, now, scope, cancellationToken);
            case RegistrationState.Approved:
                return await IssueChallengeAsync(registration, interval, now, scope, cancellationToken);
            case RegistrationState.Rejected when now < registration.ApprovalExpiresAt:
                return new PairingStatusResult("rejected", interval, registration.ApprovalExpiresAt);
            default:
                return OperationFailure.Validation(PollingHandleInvalid);
        }
    }

    private async Task<OperationResult<PairingStatusResult>> IssueChallengeAsync(
        AnalystManagerRegistration registration, double interval, DateTimeOffset now,
        IUnitOfWorkScope scope, CancellationToken cancellationToken)
    {
        var challenge = secrets.NewChallenge();
        if (!registration.IssueChallenge(secrets.Hash(challenge), now).Succeeded)
        {
            return OperationFailure.Validation(PollingHandleInvalid);
        }

        var write = await store.SetChallengeAsync(
            registration.Id, RegistrationState.Approved, registration.ChallengeHash!, registration.ChallengeExpiresAt!.Value, cancellationToken);
        if (write.Outcome != VersionedWriteOutcome.Applied)
        {
            return OperationFailure.Validation(PollingHandleInvalid);
        }

        await scope.CommitAsync(cancellationToken);
        return new PairingStatusResult("approved", interval, registration.ActivationExpiresAt, challenge, registration.ChallengeExpiresAt);
    }

    private async Task<OperationResult<PairingStatusResult>> ExpireAsync(
        AnalystManagerRegistration registration, RegistrationState expected, string reason, DateTimeOffset now,
        IUnitOfWorkScope scope, CancellationToken cancellationToken)
    {
        if (registration.Expire(reason, now).Succeeded)
        {
            var write = await store.UpdateAsync(registration, expected, true, cancellationToken);
            if (write.Outcome == VersionedWriteOutcome.Applied)
            {
                await audit.RecordAsync(RegistryAuditEvents.Expired(registration.Id, reason, new Dictionary<string, string>
                {
                    ["fromState"] = expected.ToString(),
                    ["toState"] = nameof(RegistrationState.Expired),
                }), cancellationToken);
                await scope.CommitAsync(cancellationToken);
            }
        }

        return OperationFailure.Validation(PollingHandleInvalid);
    }
}
