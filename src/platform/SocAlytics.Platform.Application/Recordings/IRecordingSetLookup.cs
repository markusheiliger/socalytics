namespace SocAlytics.Platform.Application.Recordings;

/// <summary>Database-only lookup of a finalized recording set's frozen lineage; performs no authorization.</summary>
public interface IRecordingSetLookup
{
    Task<RecordingSetLineage?> GetAsync(Guid recordingSetVersionId, CancellationToken cancellationToken);

    /// <summary>The lineage of the match's latest finalized recording set, or null when the match has none.</summary>
    Task<RecordingSetLineage?> GetCurrentForMatchAsync(Guid matchId, CancellationToken cancellationToken);
}
