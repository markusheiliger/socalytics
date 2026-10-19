using SocAlytics.Platform.Domain.AgentOrchestration;

namespace SocAlytics.Platform.Application.AgentOrchestration.Execution;

internal static class AgentAttemptGuard
{
    // The attempt must be the invocation's current active attempt with the presented token, an unexpired lease, and an unreached deadline.
    public static AgentStaleDisposition? StaleDisposition(
        AgentInvocationRecord invocation, AgentAttemptRecord attempt, string presentedToken, DateTimeOffset now)
    {
        if (AgentStateTransitions.IsTerminal(invocation.State))
        {
            return invocation.FailureReason == AgentFailureReason.ScopeLost
                ? AgentStaleDisposition.ScopeLost
                : AgentStaleDisposition.InvocationEnded;
        }

        if (attempt.State == AgentAttemptState.Fenced)
        {
            return attempt.EndReason == AgentScopeLoss.Reason ? AgentStaleDisposition.ScopeLost : AgentStaleDisposition.AttemptFenced;
        }

        if (attempt.State != AgentAttemptState.Active
            || invocation.State != AgentInvocationState.Running
            || invocation.CurrentAttemptId != attempt.Id)
        {
            return AgentStaleDisposition.AttemptNotCurrent;
        }

        if (AgentExecutionSupport.FencingTokenText(attempt.FencingToken) != presentedToken)
        {
            return AgentStaleDisposition.AttemptFenced;
        }

        return attempt.LeaseExpiresAt <= now || attempt.DeadlineAt <= now ? AgentStaleDisposition.LeaseExpired : null;
    }
}
