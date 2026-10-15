namespace SocAlytics.Platform.Application.Analysis.Results;

public sealed record AcceptedResultManifestRecord(
    Guid AcceptedResultId,
    string ContractVersion,
    string OutputType,
    long ValidityStartMs,
    long ValidityEndMs,
    int FactCount,
    string ArtifactsJson,
    string UpstreamLineageJson);

public interface IAcceptedResultManifestStore
{
    Task InsertAsync(AcceptedResultManifestRecord manifest, CancellationToken cancellationToken);

    Task<AcceptedResultManifestRecord?> GetAsync(Guid acceptedResultId, CancellationToken cancellationToken);
}
