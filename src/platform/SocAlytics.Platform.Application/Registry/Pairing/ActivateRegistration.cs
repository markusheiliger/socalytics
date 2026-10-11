using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.Registry.Abstractions;
using SocAlytics.Platform.Domain.Registry;

namespace SocAlytics.Platform.Application.Registry.Pairing;

public sealed record ActivationResult(string ManagerId, string StampId, string TokenEndpoint, string KeyThumbprint);

public sealed class ActivateRegistrationHandler(
    IUnitOfWork unitOfWork,
    IRegistrationStore store,
    IRegistrySecretGenerator secrets,
    IManagerProofValidator validator,
    IAuditTrail audit,
    IOptions<StampOptions> stamp,
    TimeProvider time)
{
    public const string ProofInvalid = "analyst-manager-activation-proof-invalid";
    public const string Expired = "analyst-manager-registration-expired";

    public async Task<OperationResult<ActivationResult>> HandleAsync(string? pollingHandle, string? proof, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(pollingHandle))
        {
            return OperationFailure.Validation(GetPairingStatusHandler.PollingHandleInvalid);
        }

        var now = time.GetUtcNow();
        await using var scope = await unitOfWork.BeginAsync(cancellationToken);
        var registration = await store.FindByPollingHandleHashAsync(secrets.Hash(pollingHandle), cancellationToken);
        if (registration is null)
        {
            return OperationFailure.Validation(GetPairingStatusHandler.PollingHandleInvalid);
        }

        switch (registration.State)
        {
            case RegistrationState.Approved:
                break;
            case RegistrationState.PendingApproval or RegistrationState.Pairing or RegistrationState.Rejected or RegistrationState.Expired:
                return OperationFailure.Conflict(RegistrationTransitionResult.InvalidStateTransition);
            default:
                return OperationFailure.Validation(GetPairingStatusHandler.PollingHandleInvalid);
        }

        if (now >= registration.ActivationExpiresAt)
        {
            if (registration.Expire(AnalystManagerRegistration.ActivationWindowElapsed, now).Succeeded
                && (await store.UpdateAsync(registration, RegistrationState.Approved, true, cancellationToken)).Outcome == VersionedWriteOutcome.Applied)
            {
                await audit.RecordAsync(RegistryAuditEvents.Expired(registration.Id, AnalystManagerRegistration.ActivationWindowElapsed, new Dictionary<string, string>
                {
                    ["fromState"] = nameof(RegistrationState.Approved),
                    ["toState"] = nameof(RegistrationState.Expired),
                }), cancellationToken);
                await scope.CommitAsync(cancellationToken);
            }

            return OperationFailure.Conflict(Expired);
        }

        var storedHash = registration.ChallengeHash;
        var storedExpiry = registration.ChallengeExpiresAt;
        var consumed = await store.ClearChallengeAsync(registration.Id, RegistrationState.Approved, cancellationToken);
        if (consumed.Outcome != VersionedWriteOutcome.Applied)
        {
            return OperationFailure.Validation(GetPairingStatusHandler.PollingHandleInvalid);
        }

        var validation = string.IsNullOrEmpty(proof)
            ? ProofValidationResult.Refused(ProofRefusalReasons.Invalid)
            : await validator.ValidateActivationProofAsync(proof, registration.PublicJwk, cancellationToken);
        var reason = validation.RefusalReason ?? ProofRefusalReasons.Invalid;
        if (validation.Succeeded
            && storedHash is not null && storedExpiry is { } expiry && now < expiry
            && validation.Challenge is { } presented
            && CryptographicOperations.FixedTimeEquals(storedHash, secrets.Hash(presented)))
        {
            var activated = registration.Activate(Guid.CreateVersion7(), now);
            if (activated.Succeeded
                && (await store.UpdateAsync(registration, RegistrationState.Approved, false, cancellationToken)).Outcome == VersionedWriteOutcome.Applied)
            {
                await audit.RecordAsync(RegistryAuditEvents.Activated(registration.Id, new Dictionary<string, string>
                {
                    ["fromState"] = nameof(RegistrationState.Approved),
                    ["toState"] = nameof(RegistrationState.Active),
                }), cancellationToken);
                await scope.CommitAsync(cancellationToken);
                return new ActivationResult(
                    registration.ManagerId!.Value.ToString(), registration.StampId, stamp.Value.TokenUri.ToString(), registration.KeyThumbprint);
            }
        }
        else if (validation.Succeeded)
        {
            reason = ProofRefusalReasons.Invalid;
        }

        await audit.RecordAsync(RegistryAuditEvents.ActivationRefused(registration.Id, reason), cancellationToken);
        await scope.CommitAsync(cancellationToken);
        return OperationFailure.Validation(ProofInvalid);
    }
}
