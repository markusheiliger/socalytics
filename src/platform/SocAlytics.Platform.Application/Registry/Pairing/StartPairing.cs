using System.Text.Json;
using Microsoft.Extensions.Options;
using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.Registry.Abstractions;
using SocAlytics.Platform.Domain.Registry;

namespace SocAlytics.Platform.Application.Registry.Pairing;

public sealed record StartPairingCommand(
    string? StampId, JsonElement PublicKey, string? Nonce, string? Os, string? OsVersion, string? Architecture,
    string? ManagerVersion, string? ClaimedKeyProtection, string? HostLabel, string? SourceAddress);

public sealed record PairingIssuedResult(
    string PairingCode, string DeviceFingerprint, string PollingHandle, Uri VerificationUri,
    DateTimeOffset ExpiresAt, double PollingIntervalSeconds, string StampId);

public sealed class StartPairingHandler(
    IUnitOfWork unitOfWork,
    IRegistrationStore store,
    IRegistrySecretGenerator secrets,
    IAuditTrail audit,
    IRequestContext context,
    IOptions<RegistryOptions> registryOptions,
    IOptions<StampOptions> stampOptions,
    TimeProvider time)
{
    public const string StampMismatch = "analyst-manager-stamp-mismatch";
    public const string KeyInUse = "analyst-manager-key-in-use";
    public const string SoftwareKeyNotAllowed = "analyst-manager-software-key-not-allowed";

    public async Task<OperationResult<PairingIssuedResult>> HandleAsync(
        StartPairingCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var registry = registryOptions.Value;
        var stamp = stampOptions.Value;

        var violations = new List<FieldViolation>();
        if (string.IsNullOrEmpty(command.StampId))
        {
            violations.Add(new("stampId", "required"));
        }

        DeviceKeyThumbprint? thumbprint = null;
        if (!DeviceKeyThumbprint.TryCreate(command.PublicKey, out thumbprint))
        {
            violations.Add(new("publicKey", DeviceKeyThumbprint.RejectionMessage));
        }

        if (command.Nonce is null || command.Nonce.Length is < 16 or > 128)
        {
            violations.Add(new("nonce", "Must be 16 to 128 characters."));
        }

        DeviceMetadata.TryCreate(
            command.Os, command.OsVersion, command.Architecture, command.ManagerVersion,
            command.ClaimedKeyProtection, command.HostLabel, out var metadata, out var metadataViolations);
        foreach (var v in metadataViolations)
        {
            violations.Add(new($"deviceMetadata.{v.Field}", v.Message));
        }

        if (violations.Count > 0)
        {
            return OperationFailure.Validation(violations.ToArray());
        }

        if (!string.Equals(command.StampId, stamp.Id, StringComparison.Ordinal))
        {
            return OperationFailure.Conflict(StampMismatch);
        }

        if (metadata!.ClaimedKeyProtection == ClaimedKeyProtection.Software && !registry.AllowSoftwareKeys)
        {
            await audit.RecordIndependentAsync(RegistryAuditEvents.PairingRefused(null, "software-key-not-allowed"), cancellationToken);
            return OperationFailure.Conflict(SoftwareKeyNotAllowed);
        }

        var now = time.GetUtcNow();
        var windows = new RegistrationWindows(
            registry.Pairing.Window, registry.Pairing.ApprovalWindow, registry.Pairing.ActivationWindow,
            registry.Pairing.ChallengeLifetime);
        var registration = AnalystManagerRegistration.Start(
            Guid.CreateVersion7(), stamp.Id, thumbprint!, command.PublicKey.GetRawText(), command.Nonce!, metadata,
            command.SourceAddress, context.CorrelationId, now, windows);
        var code = secrets.NewPairingCode();
        var handle = secrets.NewPollingHandle();

        try
        {
            await using var scope = await unitOfWork.BeginAsync(cancellationToken);
            if (await store.FindNonTerminalByThumbprintAsync(thumbprint!.Value, cancellationToken) is not null)
            {
                return OperationFailure.Conflict(KeyInUse);
            }

            await store.InsertAsync(registration, secrets.HashPairingCode(code), secrets.Hash(handle), cancellationToken);
            await audit.RecordAsync(RegistryAuditEvents.PairingIssued(registration.Id, new Dictionary<string, string>
            {
                ["stampId"] = stamp.Id,
                ["keyThumbprint"] = thumbprint.Value,
                ["deviceFingerprint"] = registration.DeviceFingerprint.Value,
                ["claimedKeyProtection"] = metadata.ClaimedKeyProtection.ToApiName(),
                ["windowExpiresAt"] = registration.PairingExpiresAt.ToString("O"),
            }), cancellationToken);
            await scope.CommitAsync(cancellationToken);
        }
        catch (UniqueViolationException)
        {
            return OperationFailure.Conflict(KeyInUse);
        }

        return new PairingIssuedResult(
            secrets.FormatPairingCode(code), registration.DeviceFingerprint.Value, handle,
            stamp.VerificationUri(registry), registration.PairingExpiresAt,
            registry.Pairing.PollingInterval.TotalSeconds, stamp.Id);
    }
}
