namespace SocAlytics.Platform.Domain.Recordings;

public sealed record RecordingVersion(
    Guid Id,
    Guid MatchId,
    Guid TeamId,
    Guid UploadSessionId,
    string ObjectKey,
    long TotalSizeBytes,
    long PartSizeBytes,
    int PartCount,
    CompositeContentDigest ContentDigest,
    RecordingDescriptor Descriptor,
    string? StorageETag,
    Guid CreatedBy,
    DateTimeOffset CreatedAt)
{
    public static RecordingVersion FromCompletedUpload(
        Guid id,
        UploadSession session,
        CompositeContentDigest verifiedDigest,
        string? storageETag,
        Guid createdBy,
        DateTimeOffset createdAt)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(verifiedDigest);

        return new RecordingVersion(
            id,
            session.MatchId,
            session.TeamId,
            session.Id,
            session.ObjectKey,
            session.Declaration.TotalSizeBytes,
            session.Declaration.PartSizeBytes,
            session.Declaration.PartCount,
            verifiedDigest,
            session.Descriptor,
            storageETag,
            createdBy,
            createdAt);
    }
}
