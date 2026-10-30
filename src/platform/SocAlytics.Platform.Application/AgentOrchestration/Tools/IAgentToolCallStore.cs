namespace SocAlytics.Platform.Application.AgentOrchestration.Tools;

public sealed record NewAgentToolCall(
    Guid AttemptId,
    string CallId,
    Guid InvocationId,
    string ToolId,
    string ArgumentsDigest,
    bool IsClipResolution,
    DateTimeOffset ReservedAt);

public sealed record AgentToolCallRecord(
    Guid AttemptId,
    string CallId,
    Guid InvocationId,
    string ToolId,
    string ArgumentsDigest,
    bool IsClipResolution,
    DateTimeOffset ReservedAt);

public sealed record AgentToolCallCounts(int Calls, int ClipResolutions);

public interface IAgentToolCallStore
{
    // False when the call identifier was already reserved for the attempt; nothing is written then.
    Task<bool> TryReserveAsync(NewAgentToolCall call, CancellationToken cancellationToken);

    Task<AgentToolCallCounts> CountAsync(Guid attemptId, CancellationToken cancellationToken);

    // Reservations without a tool-call lineage entry whose attempt is no longer active or reached its deadline, oldest first.
    Task<IReadOnlyList<AgentToolCallRecord>> ListWithoutLineageAsync(
        DateTimeOffset now, int batchSize, CancellationToken cancellationToken);
}
