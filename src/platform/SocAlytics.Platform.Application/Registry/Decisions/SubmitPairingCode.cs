using Microsoft.Extensions.Options;
using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.IdentityAccess;
using SocAlytics.Platform.Application.Registry.Abstractions;
using SocAlytics.Platform.Domain.Registry;

namespace SocAlytics.Platform.Application.Registry.Decisions;

public sealed record SubmitPairingCodeCommand(string? PairingCode, string? DeviceFingerprint);

public sealed class SubmitPairingCodeHandler(
    IUnitOfWork unitOfWork,
    IAccessAuthorizer authorizer,
    IRegistrationStore store,
    IRegistrySecretGenerator secrets,
    IAuditTrail audit,
    IRequestContext context,
    IOptions<RegistryOptions> registryOptions,
    TimeProvider time)
{
    public const string PairingCodeInvalid = "analyst-manager-pairing-code-invalid";

    public async Task<OperationResult<AnalystManagerRegistration>> HandleAsync(
        SubmitPairingCodeCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await using var scope = await unitOfWork.BeginAsync(cancellationToken);
        var decision = await authorizer.AuthorizeClubAsync(
            ClubPermission.Register, new AuditResource(RegistryAuditEvents.ResourceType, null), cancellationToken);
        if (!decision.IsGranted)
        {
            return OperationFailure.Forbidden();
        }

        var invalid = OperationFailure.Validation(PairingCodeInvalid);
        if (string.IsNullOrWhiteSpace(command.PairingCode) || command.PairingCode.Length > 32
            || string.IsNullOrWhiteSpace(command.DeviceFingerprint) || command.DeviceFingerprint.Length > 16)
        {
            return invalid;
        }

        var registration = await store.FindByPairingCodeHashAsync(secrets.HashPairingCode(command.PairingCode), cancellationToken);
        if (registration is null || registration.State != RegistrationState.Pairing)
        {
            return invalid;
        }

        var now = time.GetUtcNow();
        if (now >= registration.PairingExpiresAt)
        {
            if (registration.Expire(AnalystManagerRegistration.PairingWindowElapsed, now).Succeeded
                && (await store.UpdateAsync(registration, RegistrationState.Pairing, true, cancellationToken)).Outcome == VersionedWriteOutcome.Applied)
            {
                await audit.RecordAsync(RegistryAuditEvents.Expired(registration.Id, AnalystManagerRegistration.PairingWindowElapsed, Transition("Pairing")), cancellationToken);
                await scope.CommitAsync(cancellationToken);
            }

            return invalid;
        }

        if (!registration.DeviceFingerprint.Matches(command.DeviceFingerprint))
        {
            return await RefuseMismatchAsync(registration, now, scope, invalid, cancellationToken);
        }

        var submit = registration.Submit(context.MemberAccountId ?? Guid.Empty, now);
        if (!submit.Succeeded)
        {
            return invalid;
        }

        var write = await store.UpdateAsync(registration, RegistrationState.Pairing, true, cancellationToken);
        if (write.Outcome != VersionedWriteOutcome.Applied)
        {
            return invalid;
        }

        await audit.RecordAsync(RegistryAuditEvents.PendingRequestCreated(registration.Id, new Dictionary<string, string>
        {
            ["fromState"] = nameof(RegistrationState.Pairing),
            ["toState"] = nameof(RegistrationState.PendingApproval),
            ["deviceFingerprint"] = registration.DeviceFingerprint.Value,
            ["windowExpiresAt"] = registration.ApprovalExpiresAt!.Value.ToString("O"),
        }), cancellationToken);
        await scope.CommitAsync(cancellationToken);
        return registration.WithVersion(write.Version ?? registration.Version);
    }

    private async Task<OperationResult<AnalystManagerRegistration>> RefuseMismatchAsync(
        AnalystManagerRegistration registration, DateTimeOffset now, IUnitOfWorkScope scope,
        OperationFailure invalid, CancellationToken cancellationToken)
    {
        await audit.RecordIndependentAsync(
            RegistryAuditEvents.SubmissionRefused(registration.Id, "fingerprint-mismatch"), cancellationToken);
        var max = registryOptions.Value.Pairing.MaxFingerprintMismatches;
        if (registration.RecordFingerprintMismatch(max, now).Succeeded)
        {
            var expired = registration.State == RegistrationState.Expired;
            var write = await store.UpdateAsync(registration, RegistrationState.Pairing, expired, cancellationToken);
            if (write.Outcome == VersionedWriteOutcome.Applied)
            {
                if (expired)
                {
                    await audit.RecordAsync(
                        RegistryAuditEvents.Expired(registration.Id, AnalystManagerRegistration.FingerprintMismatchLimit, Transition("Pairing")),
                        cancellationToken);
                }

                await scope.CommitAsync(cancellationToken);
            }
        }

        return invalid;
    }

    private static Dictionary<string, string> Transition(string from) => new()
    {
        ["fromState"] = from,
        ["toState"] = nameof(RegistrationState.Expired),
    };
}
