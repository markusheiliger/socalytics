using SocAlytics.Platform.Domain.Recordings;

namespace SocAlytics.Platform.Application.Recordings;

/// <summary>An upload session together with the database clock used to derive its reported state.</summary>
public sealed record StoredUploadSession(UploadSession Session, DateTimeOffset DatabaseNow);

public interface IRecordingStore
{
    /// <summary>Inserts the session in the active unit of work.</summary>
    Task InsertUploadSessionAsync(UploadSession session, CancellationToken cancellationToken);

    Task<StoredUploadSession?> FindUploadSessionAsync(Guid matchId, Guid uploadSessionId, CancellationToken cancellationToken);
}
