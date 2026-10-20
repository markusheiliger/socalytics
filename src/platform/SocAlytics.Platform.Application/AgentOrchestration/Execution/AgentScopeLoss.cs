using System.Text.Json;
using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Domain.AgentOrchestration;

namespace SocAlytics.Platform.Application.AgentOrchestration.Execution;

// Ends work whose owner lost the execution scope; the caller holds the invocation lock (and the attempt lock for an active attempt)
// and commits. A scope-lost invocation is never retried.
internal static class AgentScopeLoss
{
    public const string Reason = "scope-lost";

    public static async Task FailQueuedAsync(
        IAgentInvocationStore invocations, IAuditTrail audit, AgentInvocationRecord invocation, string clientId,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        await EndInvocationAsync(invocations, invocation, AgentInvocationState.Queued, null, now, cancellationToken);
        await audit.RecordAsync(
            AgentOrchestrationAuditEvents.ClaimScopeLost(invocation.Id, invocation.TeamId, invocation.AgentId, clientId),
            cancellationToken);
        Telemetry.AgentOrchestrationMetrics.ScopeLost("claim-refused");
    }

    // True when the owner lost the scope and the attempt was fenced; the caller must then commit and answer scope-lost.
    public static async Task<bool> EnforceAsync(
        IInvocationScopeEvaluator scopes, IAgentInvocationStore invocations, IAgentAttemptStore attempts, IAuditTrail audit,
        AgentInvocationRecord invocation, AgentAttemptRecord attempt, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (await scopes.HasExecutionScopeAsync(invocation.OwnerMemberId, invocation.TeamId, cancellationToken))
        {
            return false;
        }

        return await FenceAsync(invocations, attempts, audit, invocation, attempt, now, cancellationToken);
    }

    // False when the attempt was no longer active, in which case nothing changed.
    public static async Task<bool> FenceAsync(
        IAgentInvocationStore invocations, IAgentAttemptStore attempts, IAuditTrail audit,
        AgentInvocationRecord invocation, AgentAttemptRecord attempt, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (!await attempts.TryTransitionAsync(attempt.Id, AgentAttemptState.Active, AgentAttemptState.Fenced, Reason, now, cancellationToken))
        {
            return false;
        }

        if (invocation.State == AgentInvocationState.Running && invocation.CurrentAttemptId == attempt.Id)
        {
            await EndInvocationAsync(invocations, invocation, AgentInvocationState.Running, attempt.Id, now, cancellationToken);
        }

        await audit.RecordAsync(
            AgentOrchestrationAuditEvents.AttemptScopeLost(
                attempt.Id, invocation.Id, invocation.TeamId, attempt.AttemptNumber, invocation.AgentId, Reason),
            cancellationToken);
        Telemetry.AgentOrchestrationMetrics.ScopeLost("attempt-fenced");
        return true;
    }

    private static async Task EndInvocationAsync(
        IAgentInvocationStore invocations, AgentInvocationRecord invocation, AgentInvocationState expected, Guid? attemptId,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        var moved = await invocations.TransitionAsync(
            invocation.Id, expected, invocation.Version, AgentInvocationState.Failed, AgentFailureReason.ScopeLost, now, cancellationToken);
        if (moved.Outcome != VersionedWriteOutcome.Applied)
        {
            throw new InvalidOperationException("The locked invocation could not be moved to failed.");
        }

        await invocations.SetCurrentAttemptAsync(invocation.Id, null, invocation.AttemptsUsed, cancellationToken);
        await invocations.AppendLineageAsync(
            new NewAgentLineageEntry(
                invocation.Id, Reason, attemptId,
                JsonSerializer.Serialize(new { attemptId, reason = Reason }), now),
            cancellationToken);
    }
}
