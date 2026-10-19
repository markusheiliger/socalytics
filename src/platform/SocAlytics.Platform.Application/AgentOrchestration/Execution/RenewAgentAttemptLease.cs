using Microsoft.Extensions.Options;
using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Domain.AgentOrchestration;

namespace SocAlytics.Platform.Application.AgentOrchestration.Execution;

public sealed record RenewAgentAttemptLeaseCommand(Guid AttemptId, string WorkloadClientId, string FencingToken);

public sealed record AgentLeaseRenewal(Guid AttemptId, DateTimeOffset LeaseExpiresAt);

public sealed class RenewAgentAttemptLeaseHandler(
    IUnitOfWork unitOfWork,
    IAgentInvocationStore invocations,
    IAgentAttemptStore attempts,
    IAuditTrail audit,
    IInvocationScopeEvaluator scopes,
    IOptionsMonitor<AgentOrchestrationOptions> options,
    TimeProvider time)
{
    public async Task<OperationResult<AgentLeaseRenewal>> HandleAsync(RenewAgentAttemptLeaseCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        // The invocation is locked before the attempt, in the same order as the claim.
        await using var scope = await unitOfWork.BeginAsync(cancellationToken);
        var unlocked = await attempts.FindAsync(command.AttemptId, cancellationToken);
        if (unlocked is null || unlocked.WorkloadClientId != command.WorkloadClientId)
        {
            return OperationFailure.NotFound();
        }

        var invocation = await invocations.LockAsync(unlocked.InvocationId, cancellationToken);
        var attempt = await attempts.LockAsync(command.AttemptId, cancellationToken);
        if (invocation is null || attempt is null)
        {
            return OperationFailure.NotFound();
        }

        var now = time.GetUtcNow();
        if (AgentAttemptGuard.StaleDisposition(invocation, attempt, command.FencingToken, now) is { } disposition)
        {
            return AgentExecutionSupport.Stale(disposition);
        }

        if (await AgentScopeLoss.EnforceAsync(scopes, invocations, attempts, audit, invocation, attempt, now, cancellationToken))
        {
            await scope.CommitAsync(cancellationToken);
            return AgentExecutionSupport.Stale(AgentStaleDisposition.ScopeLost);
        }

        var policy = AgentExecutionSupport.ResolvePolicy(invocation.ExecutionPolicyJson, options.CurrentValue);
        var extended = now + TimeSpan.FromSeconds(policy.LeaseSeconds);
        var leaseExpiresAt = extended < attempt.DeadlineAt ? extended : attempt.DeadlineAt;
        if (leaseExpiresAt > attempt.LeaseExpiresAt
            && !await attempts.TryRenewLeaseAsync(attempt.Id, leaseExpiresAt, cancellationToken))
        {
            return AgentExecutionSupport.Stale(AgentStaleDisposition.AttemptNotCurrent);
        }

        await scope.CommitAsync(cancellationToken);
        return new AgentLeaseRenewal(attempt.Id, leaseExpiresAt > attempt.LeaseExpiresAt ? leaseExpiresAt : attempt.LeaseExpiresAt);
    }
}
