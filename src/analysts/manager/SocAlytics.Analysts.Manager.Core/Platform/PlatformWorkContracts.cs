using SocAlytics.Analysts.Manager.DeviceKeys;

namespace SocAlytics.Analysts.Manager.Platform;

/// <summary>Everything a DPoP-authenticated work operation needs; the token is held in memory only.</summary>
public sealed record PlatformSession(Uri BaseUri, IDeviceKey Key, string AccessToken);

public enum WorkResultKind
{
    Success,
    Replayed,
    Obsolete,
    Conflict,
    Inactive,
    Refused,
    Transient,
}

public sealed record WorkResult<T>(WorkResultKind Kind, T? Value = null, string? Code = null) where T : class;

public sealed record QueueCapability(string Id, string Version);

public sealed record QueueCredentialsRequest(string UserPublicKey, IReadOnlyList<QueueCapability> Capabilities);

public sealed record QueueConsumer(string Capability, string Consumer);

public sealed record QueueDepthCapability(string Id, int Waiting);

public sealed record QueueDepthResponse(DateTimeOffset ObservedAt, IReadOnlyList<QueueDepthCapability> Capabilities, int Total);

/// <summary>Carries a signed user JWT; never logged.</summary>
public sealed record QueueCredentialsResponse(
    string UserJwt, DateTimeOffset ExpiresAt, string QueueUrl, string Stream,
    IReadOnlyList<QueueConsumer> Consumers, string InboxPrefix, int MaxConcurrentAnalysts,
    int? UnfitRedeliveryDelaySeconds = null);

public sealed record ClaimRequest(Guid ReadinessOccurrenceId, Guid MessageId);

public sealed record ClaimGranted(
    Guid AttemptId, Guid LogicalJobId, Guid ReadinessOccurrenceId, int AttemptNumber, long FencingToken,
    DateTimeOffset LeaseExpiresAt, int LeaseSeconds, int StaleTimeoutSeconds, int ExecutionTimeoutSeconds, bool Replayed);

public sealed record HeartbeatRequest(Guid LogicalJobId, long FencingToken, DateTimeOffset ObservedAt, string ExecutionState);

public sealed record LeaseRenewed(Guid AttemptId, DateTimeOffset LeaseExpiresAt);

public sealed record CompletionOutcome(
    Guid AttemptId, Guid LogicalJobId, string Outcome, Guid? AcceptedResultId, bool Replayed, string LogicalJobState,
    string? Reason = null);

public sealed record ResultObjectDeclaration(string Name, long SizeBytes, string Digest);

public sealed record ResultGrantsRequest(long FencingToken, IReadOnlyList<ResultObjectDeclaration> Objects);

/// <summary>A presigned write grant; the URL is a credential and is never logged.</summary>
public sealed record ResultGrant(
    string Name, string Method, string Url, IReadOnlyDictionary<string, string> RequiredHeaders, DateTimeOffset ExpiresAt)
{
    public override string ToString() => $"ResultGrant {{ Name = {Name} }}";
}

public sealed record ResultGrantsResponse(string ResultLocation, IReadOnlyList<ResultGrant> Grants);

public sealed record CompletionCapability(string Id, string Version);

public sealed record CompletionResult(string ManifestReference, string ManifestChecksum);

public sealed record CompletionFailure(string Category);

public sealed record ExecutedRuntime(
    string RuntimeProfile, string RuntimeVersion, string OperatingSystem, string Architecture, IReadOnlyList<string> Accelerators);

public sealed record CompletionCorrelation(string CorrelationId);

/// <summary>Body of contracts/analysis/attempt-completion/v1 without the idempotency key, which the client adds.</summary>
public sealed record AttemptCompletion(
    string ContractVersion, string TeamId, string MatchId, string RunId, string NodeId, string LogicalJobId,
    string AttemptId, int AttemptNumber, long FencingToken, string Outcome, CompletionResult? Result,
    CompletionFailure? Failure, CompletionCapability Capability, string SchemaVersion, ExecutedRuntime ExecutedRuntime,
    string ExecutedImageDigest, string? ExecutedModelDigest, IReadOnlyList<string> AcceptedUpstreamResultIds,
    DateTimeOffset CompletedAt, CompletionCorrelation Correlation);

public sealed record UpstreamDescriptor(QueueCapability Capability, string SchemaVersion, string EdgeKind);

/// <summary>A presigned read grant; the URL is a credential and is never logged.</summary>
public sealed record UpstreamGrant(string Name, string Url, DateTimeOffset ExpiresAt)
{
    public override string ToString() => $"UpstreamGrant {{ Name = {Name} }}";
}

public sealed record UpstreamResult(
    string AcceptedResultId, System.Text.Json.JsonElement Scope, string ManifestChecksum, System.Text.Json.JsonElement Manifest,
    IReadOnlyList<UpstreamGrant> Grants);

public sealed record UpstreamCoverageEntry(string? SegmentId, bool? Match, string State);

/// <summary>One page of contracts/analysis/upstream-results/v1.</summary>
public sealed record UpstreamResultsPage(
    string ContractVersion, UpstreamDescriptor Upstream, IReadOnlyList<UpstreamResult> Results,
    IReadOnlyList<UpstreamCoverageEntry> Coverage, string? ContinuationToken);
