namespace SocAlytics.Platform.Domain.Recordings;

public sealed class UploadSession
{
    private UploadSession(
        Guid id,
        Guid matchId,
        Guid teamId,
        RecordingDescriptor descriptor,
        MultipartDeclaration declaration,
        string objectKey,
        string multipartUploadId,
        Guid createdBy,
        DateTimeOffset createdAt,
        DateTimeOffset expiresAt,
        UploadSessionState state,
        DateTimeOffset? completedAt,
        DateTimeOffset? expiredAt,
        DateTimeOffset? storageReleasedAt,
        long version)
    {
        Id = id;
        MatchId = matchId;
        TeamId = teamId;
        Descriptor = descriptor;
        Declaration = declaration;
        ObjectKey = objectKey;
        MultipartUploadId = multipartUploadId;
        CreatedBy = createdBy;
        CreatedAt = createdAt;
        ExpiresAt = expiresAt;
        State = state;
        CompletedAt = completedAt;
        ExpiredAt = expiredAt;
        StorageReleasedAt = storageReleasedAt;
        Version = version;
    }

    public Guid Id { get; }

    public Guid MatchId { get; }

    public Guid TeamId { get; }

    public RecordingDescriptor Descriptor { get; }

    public MultipartDeclaration Declaration { get; }

    public string ObjectKey { get; }

    public string MultipartUploadId { get; }

    public Guid CreatedBy { get; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset ExpiresAt { get; }

    public UploadSessionState State { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    public DateTimeOffset? ExpiredAt { get; private set; }

    public DateTimeOffset? StorageReleasedAt { get; private set; }

    public long Version { get; }

    public static string BuildObjectKey(string prefix, Guid matchId, Guid uploadSessionId) =>
        $"{prefix}matches/{matchId}/upload-sessions/{uploadSessionId}";

    public static UploadSession Start(
        Guid id,
        Guid matchId,
        Guid teamId,
        RecordingDescriptor descriptor,
        MultipartDeclaration declaration,
        string objectKeyPrefix,
        string multipartUploadId,
        Guid createdBy,
        DateTimeOffset createdAt,
        TimeSpan sessionLifetime)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(declaration);
        ArgumentNullException.ThrowIfNull(objectKeyPrefix);
        ArgumentException.ThrowIfNullOrEmpty(multipartUploadId);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(sessionLifetime, TimeSpan.Zero);

        return new UploadSession(
            id,
            matchId,
            teamId,
            descriptor,
            declaration,
            BuildObjectKey(objectKeyPrefix, matchId, id),
            multipartUploadId,
            createdBy,
            createdAt,
            createdAt + sessionLifetime,
            UploadSessionState.Pending,
            null,
            null,
            null,
            1);
    }

    public static UploadSession Rehydrate(
        Guid id,
        Guid matchId,
        Guid teamId,
        RecordingDescriptor descriptor,
        MultipartDeclaration declaration,
        string objectKey,
        string multipartUploadId,
        Guid createdBy,
        DateTimeOffset createdAt,
        DateTimeOffset expiresAt,
        UploadSessionState state,
        DateTimeOffset? completedAt,
        DateTimeOffset? expiredAt,
        DateTimeOffset? storageReleasedAt,
        long version) =>
        new(
            id, matchId, teamId, descriptor, declaration, objectKey, multipartUploadId, createdBy,
            createdAt, expiresAt, state, completedAt, expiredAt, storageReleasedAt, version);

    public bool CanIssueGrants(DateTimeOffset now) => State == UploadSessionState.Pending && ExpiresAt > now;

    public bool CanComplete(DateTimeOffset now) => State == UploadSessionState.Pending && ExpiresAt > now;

    public bool IsDueForExpiry(DateTimeOffset now) => State == UploadSessionState.Pending && ExpiresAt <= now;

    public bool IsReportedExpired(DateTimeOffset now) =>
        State == UploadSessionState.Expired || IsDueForExpiry(now);

    public bool TryComplete(DateTimeOffset now)
    {
        if (!CanComplete(now))
        {
            return false;
        }

        State = UploadSessionState.Completed;
        CompletedAt = now;
        return true;
    }

    public bool TryExpire(DateTimeOffset now)
    {
        if (!IsDueForExpiry(now))
        {
            return false;
        }

        State = UploadSessionState.Expired;
        ExpiredAt = now;
        return true;
    }

    public bool TryMarkStorageReleased(DateTimeOffset now)
    {
        if (State != UploadSessionState.Expired || StorageReleasedAt is not null)
        {
            return false;
        }

        StorageReleasedAt = now;
        return true;
    }
}
