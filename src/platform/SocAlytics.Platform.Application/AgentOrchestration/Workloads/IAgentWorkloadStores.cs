namespace SocAlytics.Platform.Application.AgentOrchestration.Workloads;

public static class AgentWorkloadOperationKinds
{
    public const string Claim = "claim";
    public const string Checkpoint = "checkpoint";
    public const string Complete = "complete";
    public const string Fail = "fail";
}

/// <summary>A recorded workload operation; <paramref name="RequestHash"/> is the SHA-256 of the canonical request and <paramref name="Outcome"/> the recorded response as JSON.</summary>
public sealed record AgentWorkloadOperationRecord(
    string WorkloadClientId,
    string OperationId,
    string Kind,
    string RequestHash,
    Guid InvocationId,
    Guid AttemptId,
    string Outcome,
    DateTimeOffset RecordedAt);

public interface IAgentWorkloadOperationStore
{
    Task<AgentWorkloadOperationRecord?> FindAsync(
        string workloadClientId, string operationId, CancellationToken cancellationToken);

    /// <summary>Records an operation; returns false when the client already recorded the operation id.</summary>
    Task<bool> InsertAsync(AgentWorkloadOperationRecord record, CancellationToken cancellationToken);
}

/// <summary>An issued workload access token; only the SHA-256 hash of the token is stored.</summary>
public sealed record AgentWorkloadAccessToken(
    string TokenHash,
    string WorkloadClientId,
    string KeyThumbprint,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt);

public interface IAgentWorkloadTokenStore
{
    Task InsertAsync(AgentWorkloadAccessToken token, CancellationToken cancellationToken);

    Task<AgentWorkloadAccessToken?> FindByHashAsync(string tokenHash, CancellationToken cancellationToken);

    /// <summary>Deletes tokens that expired at or before <paramref name="now"/> and returns the count.</summary>
    Task<int> DeleteExpiredAsync(DateTimeOffset now, CancellationToken cancellationToken);
}
