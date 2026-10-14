using SocAlytics.Platform.Application.Abstractions.Persistence;

namespace SocAlytics.Platform.Application.IdentityAccess.Email;

public static class EmailAddressStates
{
    public const string Unverified = "unverified";
    public const string Verified = "verified";
}

public static class EmailConfirmationStates
{
    public const string Pending = "pending";
    public const string Issued = "issued";
    public const string Used = "used";
    public const string Superseded = "superseded";
    public const string Expired = "expired";
}

public static class EmailMessageStates
{
    public const string Queued = "queued";
    public const string Sending = "sending";
    public const string Sent = "sent";
    public const string Failed = "failed";
    public const string Discarded = "discarded";
}

public static class EmailErrorCategories
{
    public const string Transient = "transient";
    public const string Permanent = "permanent";
    public const string Ineligible = "ineligible";
}

public sealed record StoredMemberEmailAddress(
    Guid AccountId,
    string Address,
    string AddressNormalized,
    string State,
    DateTimeOffset? VerifiedAt,
    Guid SetBy,
    DateTimeOffset UpdatedAt,
    long Version);

public sealed record StoredEmailConfirmation(
    Guid Id,
    Guid AccountId,
    string AddressHash,
    string State,
    byte[]? TokenHash,
    DateTimeOffset? IssuedAt,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? UsedAt);

public sealed record StoredNotificationPreference(
    Guid AccountId,
    string NotificationType,
    bool Enabled,
    DateTimeOffset UpdatedAt);

public sealed record NewEmailMessage(
    Guid Id,
    string NotificationType,
    Guid AccountId,
    string AddressHash,
    Guid? ConfirmationId,
    DateTimeOffset CreatedAt,
    DateTimeOffset NextAttemptAt);

public sealed record StoredEmailMessage(
    Guid Id,
    string NotificationType,
    Guid AccountId,
    string AddressHash,
    Guid? ConfirmationId,
    string State,
    int AttemptCount,
    DateTimeOffset NextAttemptAt,
    DateTimeOffset? LeaseUntil,
    string? LastErrorCategory,
    DateTimeOffset CreatedAt,
    DateTimeOffset? SentAt);

public interface IMemberEmailAddressStore
{
    Task<StoredMemberEmailAddress?> FindAsync(Guid accountId, CancellationToken cancellationToken);

    /// <summary>Inserts an unverified address; returns false when the account already has one.</summary>
    Task<bool> InsertAsync(Guid accountId, string address, string addressNormalized, Guid setBy, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Replaces the address and resets it to unverified.</summary>
    Task<VersionedWriteResult> ReplaceAsync(Guid accountId, string address, string addressNormalized, Guid setBy, DateTimeOffset now, long expectedVersion, CancellationToken cancellationToken);

    Task<VersionedWriteResult> MarkVerifiedAsync(Guid accountId, DateTimeOffset now, long expectedVersion, CancellationToken cancellationToken);

    Task<VersionedWriteResult> DeleteAsync(Guid accountId, long expectedVersion, CancellationToken cancellationToken);
}

public interface IEmailConfirmationStore
{
    Task CreatePendingAsync(Guid id, Guid accountId, string addressHash, CancellationToken cancellationToken);

    Task<StoredEmailConfirmation?> FindAsync(Guid id, CancellationToken cancellationToken);

    Task<StoredEmailConfirmation?> FindByTokenHashAsync(byte[] tokenHash, CancellationToken cancellationToken);

    /// <summary>Stores the token hash and validity of a pending or issued confirmation; returns false when it is no longer open.</summary>
    Task<bool> IssueAsync(Guid id, byte[] tokenHash, DateTimeOffset issuedAt, DateTimeOffset expiresAt, CancellationToken cancellationToken);

    /// <summary>Marks an issued confirmation used; returns false when it is not issued.</summary>
    Task<bool> MarkUsedAsync(Guid id, DateTimeOffset usedAt, CancellationToken cancellationToken);

    Task<bool> MarkExpiredAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Supersedes every open (pending or issued) confirmation of the account; returns the number changed.</summary>
    Task<int> SupersedeOpenAsync(Guid accountId, CancellationToken cancellationToken);
}

public interface INotificationPreferenceStore
{
    Task<IReadOnlyList<StoredNotificationPreference>> ListAsync(Guid accountId, CancellationToken cancellationToken);

    Task<StoredNotificationPreference?> FindAsync(Guid accountId, string notificationType, CancellationToken cancellationToken);

    Task SetAsync(Guid accountId, string notificationType, bool enabled, DateTimeOffset now, CancellationToken cancellationToken);
}

public interface IEmailMessageStore
{
    Task QueueAsync(NewEmailMessage message, CancellationToken cancellationToken);

    Task<StoredEmailMessage?> FindAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Claims up to <paramref name="limit"/> due messages (queued, or sending with an expired lease) with FOR UPDATE SKIP LOCKED and sets the sending lease.</summary>
    Task<IReadOnlyList<StoredEmailMessage>> ClaimDueAsync(DateTimeOffset now, DateTimeOffset leaseUntil, int limit, CancellationToken cancellationToken);

    Task MarkSentAsync(Guid id, DateTimeOffset sentAt, CancellationToken cancellationToken);

    /// <summary>Returns a sending message to queued with a new attempt time after a transient failure.</summary>
    Task MarkRetryAsync(Guid id, DateTimeOffset nextAttemptAt, CancellationToken cancellationToken);

    Task MarkFailedAsync(Guid id, string errorCategory, CancellationToken cancellationToken);

    Task MarkDiscardedAsync(Guid id, string errorCategory, CancellationToken cancellationToken);

    /// <summary>Counts messages of a type for an account created at or after <paramref name="since"/>, for coalescing and the resend limit.</summary>
    Task<int> CountCreatedSinceAsync(string notificationType, Guid accountId, DateTimeOffset since, CancellationToken cancellationToken);
}
