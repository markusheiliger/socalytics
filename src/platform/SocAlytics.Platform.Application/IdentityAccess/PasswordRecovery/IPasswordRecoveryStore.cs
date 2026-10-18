namespace SocAlytics.Platform.Application.IdentityAccess.PasswordRecovery;

public sealed record StoredPasswordRecovery(
    Guid Id,
    Guid AccountId,
    string AddressHash,
    string SecurityStamp,
    string State,
    byte[]? TokenHash,
    DateTimeOffset RequestedAt,
    DateTimeOffset? IssuedAt,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? UsedAt);

/// <summary>A member whose current email address normalizes to the requested value.</summary>
public sealed record PasswordRecoveryCandidate(
    Guid AccountId,
    string AccountName,
    string MembershipStatus,
    bool HasPassword,
    string SecurityStamp,
    string AddressState,
    string AddressHash);

public interface IPasswordRecoveryStore
{
    /// <summary>Lists every member whose current address equals the normalized value; the query is the same for unknown addresses.</summary>
    Task<IReadOnlyList<PasswordRecoveryCandidate>> ListCandidatesAsync(string addressNormalized, CancellationToken cancellationToken);

    Task CreatePendingAsync(
        Guid id, Guid accountId, string addressHash, string securityStamp, DateTimeOffset requestedAt, CancellationToken cancellationToken);

    /// <summary>Supersedes every open (pending or issued) recovery of the account; returns the number changed.</summary>
    Task<int> SupersedeOpenAsync(Guid accountId, CancellationToken cancellationToken);

    Task<StoredPasswordRecovery?> FindAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Finds a recovery by token hash and locks its row until the unit of work ends.</summary>
    Task<StoredPasswordRecovery?> FindByTokenHashForUpdateAsync(byte[] tokenHash, CancellationToken cancellationToken);

    /// <summary>Stores the token hash and validity of a pending or issued recovery; returns false when it is no longer open.</summary>
    Task<bool> IssueAsync(Guid id, byte[] tokenHash, DateTimeOffset issuedAt, DateTimeOffset expiresAt, CancellationToken cancellationToken);

    /// <summary>Marks an issued recovery used; returns false when it is not issued.</summary>
    Task<bool> MarkUsedAsync(Guid id, DateTimeOffset usedAt, CancellationToken cancellationToken);

    /// <summary>Marks a pending or issued recovery invalidated; returns false when it is no longer open.</summary>
    Task<bool> MarkInvalidatedAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Marks an issued recovery expired; returns false when it is not issued.</summary>
    Task<bool> MarkExpiredAsync(Guid id, CancellationToken cancellationToken);
}
