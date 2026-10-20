using SocAlytics.Platform.Domain.AgentOrchestration;

namespace SocAlytics.Platform.Application.AgentOrchestration.Invocations;

public sealed record SubmittedAgentInvocation(Guid ConversationId, Guid InvocationId, int RetryAfterSeconds, bool Replayed);

public sealed record AgentInvocationView(
    AgentInvocationRecord Invocation,
    AgentAcceptedAdviceRecord? Advice,
    int? RetryAfterSeconds);

public sealed record AgentTurnPage(IReadOnlyList<AgentTurnRecord> Turns, string? NextCursor);
