namespace SocAlytics.Platform.Application.Analysis.Stores;

public sealed record AcceptedResultRecord(
    Guid Id,
    Guid LogicalJobId,
    Guid RunId,
    Guid NodeId,
    Guid AttemptId,
    string ManifestReference,
    string ManifestChecksum,
    string CapabilityId,
    string CapabilityVersion,
    string SchemaVersion,
    string AnalystProfileDigest,
    string ImageDigest,
    string? ModelDigest,
    string PolicyVersionsJson,
    string ExecutedRuntimeJson,
    IReadOnlyList<Guid> UpstreamResultIds,
    DateTimeOffset AcceptedAt);

public interface IAcceptedResultStore
{
    Task InsertAsync(AcceptedResultRecord result, CancellationToken cancellationToken);

    Task<AcceptedResultRecord?> GetByJobAsync(Guid logicalJobId, CancellationToken cancellationToken);

    Task<IReadOnlyList<AcceptedResultRecord>> ListByRunAsync(Guid runId, CancellationToken cancellationToken);
}
