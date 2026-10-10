using SocAlytics.Platform.Domain.Recordings;

namespace SocAlytics.Platform.Domain.Registry;

public readonly record struct RegistrationTransitionResult(string? Violation)
{
    public const string InvalidStateTransition = "invalid-state-transition";
    public const string RegistrationExpired = "registration-expired";
    public const string ReasonInvalid = "reason-invalid";

    public bool Succeeded => Violation is null;

    public static RegistrationTransitionResult Ok { get; } = new((string?)null);

    public static RegistrationTransitionResult Fail(string violation) => new(violation);
}

public sealed class AnalystManagerRegistration
{
    public const string PairingWindowElapsed = "pairing-window-elapsed";
    public const string ApprovalWindowElapsed = "approval-window-elapsed";
    public const string ActivationWindowElapsed = "activation-window-elapsed";
    public const string FingerprintMismatchLimit = "fingerprint-mismatch-limit";

    private readonly RegistrationWindows _windows;

    private AnalystManagerRegistration(
        Guid id, string stampId, string publicJwk, DeviceKeyThumbprint thumbprint,
        DeviceFingerprint fingerprint, string pairingNonce, DeviceMetadata metadata,
        string? pairingSourceAddress, string correlationId, DateTimeOffset createdAt,
        RegistrationWindows windows)
    {
        Id = id;
        StampId = stampId;
        PublicJwk = publicJwk;
        KeyThumbprint = thumbprint.Value;
        DeviceFingerprint = fingerprint;
        PairingNonce = pairingNonce;
        DeviceMetadata = metadata;
        PairingSourceAddress = pairingSourceAddress;
        CorrelationId = correlationId;
        CreatedAt = createdAt;
        PairingExpiresAt = createdAt + windows.Pairing;
        _windows = windows;
        State = RegistrationState.Pairing;
    }

    public Guid Id { get; }

    public Guid? ManagerId { get; private set; }

    public string StampId { get; }

    public RegistrationState State { get; private set; }

    public string PublicJwk { get; }

    public string KeyThumbprint { get; }

    public DeviceFingerprint DeviceFingerprint { get; }

    public int FingerprintMismatchCount { get; private set; }

    public string? PairingSourceAddress { get; }

    public string PairingNonce { get; }

    public DeviceMetadata DeviceMetadata { get; }

    public byte[]? ChallengeHash { get; private set; }

    public DateTimeOffset? ChallengeExpiresAt { get; private set; }

    public DateTimeOffset PairingExpiresAt { get; }

    public DateTimeOffset? ApprovalExpiresAt { get; private set; }

    public DateTimeOffset? ActivationExpiresAt { get; private set; }

    public Guid? SubmittedBy { get; private set; }

    public DateTimeOffset? SubmittedAt { get; private set; }

    public Guid? DecidedBy { get; private set; }

    public DateTimeOffset? DecidedAt { get; private set; }

    public string? DecisionReason { get; private set; }

    public DateTimeOffset? ActivatedAt { get; private set; }

    public DateTimeOffset? UnregistrationStartedAt { get; private set; }

    public DateTimeOffset? EndedAt { get; private set; }

    public Guid? RevokedBy { get; private set; }

    public string? EndReason { get; private set; }

    public string CorrelationId { get; }

    public DateTimeOffset CreatedAt { get; }

    public static AnalystManagerRegistration Start(
        Guid id, string stampId, DeviceKeyThumbprint thumbprint, string publicJwk, string pairingNonce,
        DeviceMetadata metadata, string? pairingSourceAddress, string correlationId,
        DateTimeOffset now, RegistrationWindows windows)
    {
        ArgumentException.ThrowIfNullOrEmpty(stampId);
        ArgumentNullException.ThrowIfNull(thumbprint);
        ArgumentException.ThrowIfNullOrEmpty(publicJwk);
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentException.ThrowIfNullOrEmpty(correlationId);
        ArgumentNullException.ThrowIfNull(windows);
        if (pairingNonce is null || pairingNonce.Length is < 16 or > 128)
        {
            throw new ArgumentException("Nonce must be 16 to 128 characters.", nameof(pairingNonce));
        }

        return new AnalystManagerRegistration(
            id, stampId, publicJwk, thumbprint, DeviceFingerprint.From(thumbprint), pairingNonce,
            metadata, pairingSourceAddress, correlationId, now, windows);
    }

    public RegistrationTransitionResult Submit(Guid submittedBy, DateTimeOffset now)
    {
        var guard = Guard(RegistrationState.Pairing, PairingExpiresAt, now);
        if (!guard.Succeeded)
        {
            return guard;
        }

        State = RegistrationState.PendingApproval;
        SubmittedBy = submittedBy;
        SubmittedAt = now;
        ApprovalExpiresAt = now + _windows.Approval;
        return RegistrationTransitionResult.Ok;
    }

    // Reaching the limit expires the pairing; the counter still advances.
    public RegistrationTransitionResult RecordFingerprintMismatch(int max, DateTimeOffset now)
    {
        var guard = Guard(RegistrationState.Pairing, PairingExpiresAt, now);
        if (!guard.Succeeded)
        {
            return guard;
        }

        FingerprintMismatchCount++;
        if (FingerprintMismatchCount >= max)
        {
            End(RegistrationState.Expired, now, FingerprintMismatchLimit);
        }

        return RegistrationTransitionResult.Ok;
    }

    public RegistrationTransitionResult Approve(Guid decidedBy, DateTimeOffset now)
    {
        var guard = Guard(RegistrationState.PendingApproval, ApprovalExpiresAt, now);
        if (!guard.Succeeded)
        {
            return guard;
        }

        State = RegistrationState.Approved;
        DecidedBy = decidedBy;
        DecidedAt = now;
        ActivationExpiresAt = now + _windows.Activation;
        return RegistrationTransitionResult.Ok;
    }

    public RegistrationTransitionResult Reject(Guid decidedBy, string? reason, DateTimeOffset now)
    {
        var guard = Guard(RegistrationState.PendingApproval, ApprovalExpiresAt, now);
        if (!guard.Succeeded)
        {
            return guard;
        }

        if (!IsValidReason(reason))
        {
            return RegistrationTransitionResult.Fail(RegistrationTransitionResult.ReasonInvalid);
        }

        DecidedBy = decidedBy;
        DecidedAt = now;
        DecisionReason = reason;
        End(RegistrationState.Rejected, now, reason);
        return RegistrationTransitionResult.Ok;
    }

    public RegistrationTransitionResult IssueChallenge(byte[] challengeHash, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(challengeHash);
        var guard = Guard(RegistrationState.Approved, ActivationExpiresAt, now);
        if (!guard.Succeeded)
        {
            return guard;
        }

        ChallengeHash = challengeHash;
        ChallengeExpiresAt = now + _windows.ChallengeLifetime;
        return RegistrationTransitionResult.Ok;
    }

    public RegistrationTransitionResult Activate(Guid managerId, DateTimeOffset now)
    {
        var guard = Guard(RegistrationState.Approved, ActivationExpiresAt, now);
        if (!guard.Succeeded)
        {
            return guard;
        }

        State = RegistrationState.Active;
        ManagerId = managerId;
        ActivatedAt = now;
        ChallengeHash = null;
        ChallengeExpiresAt = null;
        return RegistrationTransitionResult.Ok;
    }

    public RegistrationTransitionResult Expire(string reason, DateTimeOffset now)
    {
        if (State is not (RegistrationState.Pairing or RegistrationState.PendingApproval or RegistrationState.Approved))
        {
            return RegistrationTransitionResult.Fail(RegistrationTransitionResult.InvalidStateTransition);
        }

        End(RegistrationState.Expired, now, reason);
        return RegistrationTransitionResult.Ok;
    }

    public RegistrationTransitionResult BeginUnregistration(DateTimeOffset now)
    {
        if (State == RegistrationState.Unregistering)
        {
            return RegistrationTransitionResult.Ok;
        }

        if (State != RegistrationState.Active)
        {
            return RegistrationTransitionResult.Fail(RegistrationTransitionResult.InvalidStateTransition);
        }

        State = RegistrationState.Unregistering;
        UnregistrationStartedAt = now;
        return RegistrationTransitionResult.Ok;
    }

    public RegistrationTransitionResult CompleteUnregistration(DateTimeOffset now)
    {
        if (State != RegistrationState.Unregistering)
        {
            return RegistrationTransitionResult.Fail(RegistrationTransitionResult.InvalidStateTransition);
        }

        End(RegistrationState.Unregistered, now, null);
        return RegistrationTransitionResult.Ok;
    }

    public RegistrationTransitionResult Revoke(Guid revokedBy, string? reason, DateTimeOffset now)
    {
        if (State is not (RegistrationState.Active or RegistrationState.Unregistering))
        {
            return RegistrationTransitionResult.Fail(RegistrationTransitionResult.InvalidStateTransition);
        }

        if (!IsValidReason(reason))
        {
            return RegistrationTransitionResult.Fail(RegistrationTransitionResult.ReasonInvalid);
        }

        RevokedBy = revokedBy;
        End(RegistrationState.Revoked, now, reason);
        return RegistrationTransitionResult.Ok;
    }

    public static FieldViolation? ValidateReason(string? reason) =>
        IsValidReason(reason) ? null : new FieldViolation("reason", "Required (1–500 chars).");

    private static bool IsValidReason(string? reason) =>
        !string.IsNullOrWhiteSpace(reason) && reason.Length <= 500;

    private RegistrationTransitionResult Guard(RegistrationState expected, DateTimeOffset? expiresAt, DateTimeOffset now)
    {
        if (State != expected)
        {
            return RegistrationTransitionResult.Fail(RegistrationTransitionResult.InvalidStateTransition);
        }

        return expiresAt is { } limit && now >= limit
            ? RegistrationTransitionResult.Fail(RegistrationTransitionResult.RegistrationExpired)
            : RegistrationTransitionResult.Ok;
    }

    private void End(RegistrationState terminal, DateTimeOffset now, string? reason)
    {
        State = terminal;
        EndedAt = now;
        EndReason = reason;
        ChallengeHash = null;
        ChallengeExpiresAt = null;
    }
}
