namespace SocAlytics.Platform.Application.Analysis.Facts;

public sealed record FactRunRecord(
    Guid RunId,
    Guid MatchId,
    Guid TeamId,
    string State,
    string WorkflowId,
    string WorkflowVersion,
    long SegmentDurationMs,
    long? RecordingCoverageEndMs,
    string VersionBasis);

public sealed record FactNodeRecord(
    Guid NodeId,
    string NodeKey,
    string CapabilityId,
    string CapabilityVersion,
    string SchemaVersion,
    string OutputType,
    string ExecutionScope,
    string AnalystProfileId,
    string AnalystProfileDigest,
    string ImageDigest,
    string? ModelDigest,
    bool Required);

public sealed record FactPageKey(long StartMs, long EndMs, Guid AcceptedResultId, int Ordinal);

public sealed record FactPageQuery(
    Guid RunId, Guid NodeId, long? FromMs, long? ToMs, FactPageKey? After, int PageSize);

public sealed record FactItemRecord(
    Guid AcceptedResultId,
    int Ordinal,
    string Kind,
    long StartMs,
    long EndMs,
    double Confidence,
    string PayloadJson);

public sealed record FactPageResult(IReadOnlyList<FactItemRecord> Items, bool HasMore);

public sealed record FactCoverageJobRecord(Guid NodeId, int? SegmentNumber, string State, bool HasAcceptedResult);

public sealed record FactCoverageInputs(
    IReadOnlyList<FactCoverageJobRecord> Jobs, long SegmentDurationMs, long? RecordingCoverageEndMs);

public interface IFactQueries
{
    Task<FactRunRecord?> GetRunAsync(Guid runId, CancellationToken cancellationToken);

    /// <summary>
    /// Nodes of the run with the capability and version whose pinned output schema version equals <paramref name="schemaVersion"/>,
    /// optionally narrowed to one node key, ordered by node ordinal.
    /// </summary>
    Task<IReadOnlyList<FactNodeRecord>> ResolveNodesAsync(
        Guid runId, string capabilityId, string capabilityVersion, string schemaVersion, string? nodeKey,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<FactNodeRecord>> ListNodesAsync(Guid runId, CancellationToken cancellationToken);

    Task<FactPageResult> QueryPageAsync(FactPageQuery query, CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<Guid, long>> CountFactsByNodeAsync(Guid runId, CancellationToken cancellationToken);

    /// <summary>Raw coverage inputs of the run's nodes; <paramref name="nodeId"/> narrows them to one node.</summary>
    Task<FactCoverageInputs> GetCoverageInputsAsync(Guid runId, Guid? nodeId, CancellationToken cancellationToken);
}
