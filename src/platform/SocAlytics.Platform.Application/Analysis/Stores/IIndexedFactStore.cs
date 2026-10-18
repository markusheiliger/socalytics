namespace SocAlytics.Platform.Application.Analysis.Stores;

public sealed record IndexedFactRecord(
    Guid AcceptedResultId,
    int Ordinal,
    Guid RunId,
    Guid NodeId,
    Guid TeamId,
    Guid MatchId,
    string CapabilityId,
    string CapabilityVersion,
    string SchemaVersion,
    string OutputType,
    string Kind,
    long StartMs,
    long EndMs,
    double Confidence,
    string PayloadJson);

public interface IIndexedFactStore
{
    Task InsertAsync(IReadOnlyList<IndexedFactRecord> facts, CancellationToken cancellationToken);
}
