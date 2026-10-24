using SocAlytics.Platform.Domain.AgentOrchestration;
using SocAlytics.Platform.Domain.AgentOrchestration.Catalog;

namespace SocAlytics.Platform.Application.AgentOrchestration.Invocations;

public sealed record SubmittedAgentInvocation(Guid ConversationId, Guid InvocationId, int RetryAfterSeconds, bool Replayed);

// The agent version and artifacts an invocation was pinned to; digests and identities only, never content.
public sealed record AgentPinView(string Version, string Digest, IReadOnlyList<InvocationPin> Artifacts);

public sealed record AgentInvocationView(
    AgentInvocationRecord Invocation,
    AgentAcceptedAdviceRecord? Advice,
    int? RetryAfterSeconds,
    AgentPinView? Agent = null);

public sealed record AgentTurnPage(IReadOnlyList<AgentTurnRecord> Turns, string? NextCursor);
