using SocAlytics.Analysts.Manager.Platform;

namespace SocAlytics.Analysts.Manager.Work;

/// <summary>The parts of contracts/analysis/analyst-job/v1 the Manager reads; the payload confers no authority.</summary>
public sealed record AnalystJobMessage(
    string MessageId,
    string ReadinessOccurrenceId,
    string RunId,
    string NodeId,
    string LogicalJobId,
    string TeamId,
    string MatchId,
    QueueCapability Capability,
    string SchemaVersion,
    string ExecutionScope,
    IReadOnlyList<JobInput>? Inputs,
    JobPinned Pinned,
    JobResources Resources,
    CompletionCorrelation Correlation,
    string? RecordingSetVersionId = null,
    JobSegment? Segment = null,
    JobSegmentMedia? SegmentMedia = null);

/// <summary>The pinned materialized segment media of a segment-scoped job (analyst-job 1.2.0).</summary>
public sealed record JobSegmentMedia(string VideoPolicy, string ManifestChecksum);

public sealed record JobSegment(string RecordingVersionId, int SegmentNumber);

public sealed record JobInput(
    IReadOnlyList<JobAcceptedResult>? AcceptedResults, QueueCapability? Capability = null, string? EdgeKind = null, string? Status = null);

public sealed record JobAcceptedResult(
    string AcceptedResultId, QueueCapability? Capability = null, string? SchemaVersion = null, string? ManifestChecksum = null);

public sealed record JobPolicy(string PolicyId, string Version);

public sealed record JobPinned(
    string AnalystProfileDigest, string ImageDigest, string? ModelDigest,
    string? AnalystProfileId = null, IReadOnlyList<JobPolicy>? Policies = null);

public sealed record JobResources(int CpuMillicores, long MemoryMebibytes);
