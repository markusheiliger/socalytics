using System.Text.Json;
using Microsoft.Extensions.Options;
using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.AgentOrchestration.Workloads;
using SocAlytics.Platform.Application.Registry.QueueCredentials;
using SocAlytics.Platform.Application.Telemetry;
using SocAlytics.Platform.Domain.AgentOrchestration;

namespace SocAlytics.Platform.Application.AgentOrchestration.Execution;

public sealed record AgentMonitorResult(int Expired, int Requeued, int Exhausted, int TokensDeleted, int ScopeLost = 0);

public sealed class MonitorAgentAttemptsHandler(
    IUnitOfWork unitOfWork,
    IAgentInvocationStore invocations,
    IAgentAttemptStore attempts,
    IAgentWorkloadTokenStore tokens,
    IOutbox outbox,
    IContractValidator validator,
    IQueueCredentialIssuer queue,
    IAuditTrail audit,
    IInvocationScopeEvaluator scopes,
    IOptionsMonitor<AgentOrchestrationOptions> options,
    TimeProvider time)
{
    public const int BatchSize = 50;

    public async Task<AgentMonitorResult> HandleAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<AgentExpiredAttemptRef> due;
        await using (var read = await unitOfWork.BeginAsync(cancellationToken))
        {
            due = await attempts.ListExpiredAsync(time.GetUtcNow(), BatchSize, cancellationToken);
        }

        int expired = 0, requeued = 0, exhausted = 0;
        foreach (var item in due)
        {
            var outcome = await ExpireAsync(item, cancellationToken);
            if (outcome is null)
            {
                continue;
            }

            expired++;
            if (outcome.Value)
            {
                exhausted++;
            }
            else
            {
                requeued++;
            }
        }

        var scopeLost = await EnforceScopesAsync(cancellationToken);

        int deleted;
        await using (var scope = await unitOfWork.BeginAsync(cancellationToken))
        {
            deleted = await tokens.DeleteExpiredAsync(time.GetUtcNow(), cancellationToken);
            await scope.CommitAsync(cancellationToken);
        }

        return new AgentMonitorResult(expired, requeued, exhausted, deleted, scopeLost);
    }

    // Fences every active attempt whose owner lost the execution scope, without any workload call.
    private async Task<int> EnforceScopesAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<AgentActiveAttemptRef> active;
        await using (var read = await unitOfWork.BeginAsync(cancellationToken))
        {
            active = await attempts.ListActiveForScopeCheckAsync(cancellationToken);
        }

        var lost = 0;
        foreach (var item in active)
        {
            if (await scopes.HasExecutionScopeAsync(item.OwnerMemberId, item.TeamId, cancellationToken))
            {
                continue;
            }

            await using var scope = await unitOfWork.BeginAsync(cancellationToken);
            var invocation = await invocations.LockAsync(item.InvocationId, cancellationToken);
            var attempt = await attempts.LockAsync(item.AttemptId, cancellationToken);
            if (invocation is null || attempt is null || attempt.State != AgentAttemptState.Active)
            {
                continue;
            }

            // Re-checked under the lock so a concurrent restoration of the role wins.
            if (await AgentScopeLoss.EnforceAsync(
                    scopes, invocations, attempts, audit, invocation, attempt, time.GetUtcNow(), cancellationToken))
            {
                await scope.CommitAsync(cancellationToken);
                lost++;
            }
        }

        return lost;
    }

    // Null when the attempt was no longer expired and active; otherwise true when the invocation was exhausted.
    private async Task<bool?> ExpireAsync(AgentExpiredAttemptRef item, CancellationToken cancellationToken)
    {
        await using var scope = await unitOfWork.BeginAsync(cancellationToken);

        // The invocation is locked before the attempt, in the same order as claims and completions.
        var invocation = await invocations.LockAsync(item.InvocationId, cancellationToken);
        var attempt = await attempts.LockAsync(item.AttemptId, cancellationToken);
        if (invocation is null || attempt is null || attempt.State != AgentAttemptState.Active)
        {
            return null;
        }

        var now = time.GetUtcNow();
        if (attempt.LeaseExpiresAt > now && attempt.DeadlineAt > now)
        {
            return null;
        }

        var reason = attempt.DeadlineAt <= now ? "deadline-reached" : "lease-expired";
        if (!await attempts.TryTransitionAsync(attempt.Id, AgentAttemptState.Active, AgentAttemptState.Expired, reason, now, cancellationToken))
        {
            return null;
        }

        await invocations.AppendLineageAsync(
            new NewAgentLineageEntry(
                invocation.Id, reason, attempt.Id,
                JsonSerializer.Serialize(new { attemptId = attempt.Id, attemptNumber = attempt.AttemptNumber }), now),
            cancellationToken);
        await audit.RecordAsync(
            AgentOrchestrationAuditEvents.AttemptExpired(
                attempt.Id, invocation.Id, invocation.TeamId, attempt.AttemptNumber, invocation.AgentId, reason),
            cancellationToken);
        AgentOrchestrationMetrics.LeaseExpired(reason);

        // A concurrent end of the invocation leaves only the attempt to close.
        if (invocation.State != AgentInvocationState.Running || invocation.CurrentAttemptId != attempt.Id)
        {
            await scope.CommitAsync(cancellationToken);
            return false;
        }

        var policy = AgentExecutionSupport.ResolvePolicy(invocation.ExecutionPolicyJson, options.CurrentValue);
        var isExhausted = !policy.HasAttemptsRemaining(invocation.AttemptsUsed);
        if (isExhausted)
        {
            const string exhaustedReason = "attempts-exhausted";
            var failed = await invocations.TransitionAsync(
                invocation.Id, AgentInvocationState.Running, invocation.Version, AgentInvocationState.Failed,
                AgentFailureReason.AttemptsExhausted, now, cancellationToken);
            if (failed.Outcome != VersionedWriteOutcome.Applied)
            {
                throw new InvalidOperationException("The locked invocation could not be moved to failed.");
            }

            await invocations.SetCurrentAttemptAsync(invocation.Id, null, invocation.AttemptsUsed, cancellationToken);
            await invocations.AppendLineageAsync(
                new NewAgentLineageEntry(
                    invocation.Id, "exhausted", attempt.Id,
                    JsonSerializer.Serialize(new { attemptsUsed = invocation.AttemptsUsed, reason = exhaustedReason }), now),
                cancellationToken);
            await audit.RecordAsync(
                AgentOrchestrationAuditEvents.InvocationExhausted(
                    invocation.Id, invocation.TeamId, invocation.AgentId, attempt.AttemptNumber, exhaustedReason),
                cancellationToken);
            AgentOrchestrationMetrics.FailureRecorded(exhaustedReason);
        }
        else
        {
            var queued = await invocations.TransitionAsync(
                invocation.Id, AgentInvocationState.Running, invocation.Version, AgentInvocationState.Queued, null, null, cancellationToken);
            if (queued.Outcome != VersionedWriteOutcome.Applied)
            {
                throw new InvalidOperationException("The locked invocation could not be moved to queued.");
            }

            var occurrence = invocation.ReadyOccurrence + 1;
            await invocations.SetCurrentAttemptAsync(invocation.Id, null, invocation.AttemptsUsed, cancellationToken);
            await invocations.SetReadyOccurrenceAsync(invocation.Id, occurrence, cancellationToken);
            await AgentReadyNotification.EnqueueAsync(outbox, validator, queue, invocation, occurrence, cancellationToken);
            await invocations.SetLastNotifiedAsync(invocation.Id, now, cancellationToken);
        }

        await scope.CommitAsync(cancellationToken);
        return isExhausted;
    }
}
