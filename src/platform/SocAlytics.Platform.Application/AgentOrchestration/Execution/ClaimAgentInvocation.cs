using System.Text.Json;
using Microsoft.Extensions.Options;
using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.AgentOrchestration.Workloads;
using SocAlytics.Platform.Application.Telemetry;
using SocAlytics.Platform.Domain.AgentOrchestration;

namespace SocAlytics.Platform.Application.AgentOrchestration.Execution;

public sealed record ClaimAgentInvocationCommand(Guid InvocationId, string WorkloadClientId, string OperationId);

/// <summary>The granted <c>execution-envelope/v1</c> document; <paramref name="Replayed"/> is true when the operation was recorded before.</summary>
public sealed record AgentClaimGrant(JsonElement Envelope, bool Replayed);

public sealed class ClaimAgentInvocationHandler(
    IUnitOfWork unitOfWork,
    IAgentInvocationStore invocations,
    IAgentAttemptStore attempts,
    IAgentWorkloadOperationStore operations,
    IContractValidator validator,
    IAuditTrail audit,
    IInvocationScopeEvaluator scopes,
    IOptionsMonitor<AgentOrchestrationOptions> options,
    TimeProvider time)
{
    public async Task<OperationResult<AgentClaimGrant>> HandleAsync(ClaimAgentInvocationCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        await using var scope = await unitOfWork.BeginAsync(cancellationToken);
        var invocation = await invocations.LockAsync(command.InvocationId, cancellationToken);
        if (invocation is null)
        {
            return OperationFailure.NotFound();
        }

        var requestHash = AgentExecutionSupport.Hash($"{AgentWorkloadOperationKinds.Claim}|{command.InvocationId:D}");
        var recorded = await operations.FindAsync(command.WorkloadClientId, command.OperationId, cancellationToken);
        var policy = AgentExecutionSupport.ResolvePolicy(invocation.ExecutionPolicyJson, options.CurrentValue);
        if (recorded is not null)
        {
            if (recorded.RequestHash != requestHash || recorded.Kind != AgentWorkloadOperationKinds.Claim)
            {
                return OperationFailure.Conflict(AgentExecutionCodes.OperationIdReused);
            }

            var granted = await attempts.FindAsync(recorded.AttemptId, cancellationToken)
                ?? throw new InvalidOperationException("The recorded claim has no attempt.");
            AgentOrchestrationMetrics.ClaimRecorded("replayed");
            return new AgentClaimGrant(await BuildEnvelopeAsync(invocation, granted, policy, cancellationToken), true);
        }

        var now = time.GetUtcNow();
        var refusal = await RefusalAsync(invocation, policy, now, cancellationToken);
        if (refusal is not null)
        {
            await audit.RecordAsync(
                AgentOrchestrationAuditEvents.ClaimRefused(
                    invocation.Id, invocation.TeamId, invocation.AgentId, command.WorkloadClientId, refusal),
                cancellationToken);
            AgentOrchestrationMetrics.ClaimRecorded("refused");
            await scope.CommitAsync(cancellationToken);
            return OperationFailure.Conflict(AgentExecutionCodes.ClaimObsolete);
        }

        if (!await scopes.HasExecutionScopeAsync(invocation.OwnerMemberId, invocation.TeamId, cancellationToken))
        {
            await AgentScopeLoss.FailQueuedAsync(invocations, audit, invocation, command.WorkloadClientId, now, cancellationToken);
            await scope.CommitAsync(cancellationToken);
            return OperationFailure.Conflict(AgentExecutionCodes.ScopeLost);
        }

        var attemptNumber = invocation.AttemptsUsed + 1;
        var attempt = await attempts.InsertAsync(
            new NewAgentAttempt(
                Guid.CreateVersion7(), invocation.Id, attemptNumber, command.WorkloadClientId, now,
                now + TimeSpan.FromSeconds(policy.LeaseSeconds), now + TimeSpan.FromSeconds(policy.MaxAttemptSeconds)),
            cancellationToken);
        if (!await attempts.TryTransitionAsync(attempt.Id, AgentAttemptState.Claimed, AgentAttemptState.Active, null, null, cancellationToken))
        {
            throw new InvalidOperationException("The new attempt could not be activated.");
        }

        var moved = await invocations.TransitionAsync(
            invocation.Id, AgentInvocationState.Queued, invocation.Version, AgentInvocationState.Running, null, null, cancellationToken);
        if (moved.Outcome != VersionedWriteOutcome.Applied)
        {
            throw new InvalidOperationException("The locked invocation could not be moved to running.");
        }

        await invocations.SetCurrentAttemptAsync(invocation.Id, attempt.Id, attemptNumber, cancellationToken);
        await invocations.AppendLineageAsync(
            new NewAgentLineageEntry(
                invocation.Id, "claimed", attempt.Id,
                JsonSerializer.Serialize(new { attemptId = attempt.Id, attemptNumber }), now),
            cancellationToken);
        await operations.InsertAsync(
            new AgentWorkloadOperationRecord(
                command.WorkloadClientId, command.OperationId, AgentWorkloadOperationKinds.Claim, requestHash, invocation.Id, attempt.Id,
                JsonSerializer.Serialize(new { attemptId = attempt.Id, attemptNumber }), now),
            cancellationToken);
        await audit.RecordAsync(
            AgentOrchestrationAuditEvents.AttemptClaimed(
                attempt.Id, invocation.Id, invocation.TeamId, attemptNumber, invocation.AgentId, command.WorkloadClientId),
            cancellationToken);
        var envelope = await BuildEnvelopeAsync(invocation, attempt with { State = AgentAttemptState.Active }, policy, cancellationToken);
        await scope.CommitAsync(cancellationToken);
        AgentOrchestrationMetrics.ClaimRecorded("granted");
        return new AgentClaimGrant(envelope, false);
    }

    private async Task<string?> RefusalAsync(
        AgentInvocationRecord invocation, ExecutionPolicy policy, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (invocation.State != AgentInvocationState.Queued)
        {
            return "invocation-not-queued";
        }

        if (invocation.CurrentAttemptId is { } currentId)
        {
            var current = await attempts.FindAsync(currentId, cancellationToken);
            if (current is { State: AgentAttemptState.Active } && current.LeaseExpiresAt > now)
            {
                return "attempt-active";
            }
        }

        return policy.HasAttemptsRemaining(invocation.AttemptsUsed) ? null : "attempts-exhausted";
    }

    private async Task<JsonElement> BuildEnvelopeAsync(
        AgentInvocationRecord invocation, AgentAttemptRecord attempt, ExecutionPolicy policy, CancellationToken cancellationToken)
    {
        var history = await invocations.ListSucceededTurnsBeforeAsync(
            invocation.ConversationId, invocation.SubmittedAt, policy.HistoryTurns, cancellationToken);
        var envelope = JsonSerializer.SerializeToElement(
            new
            {
                schemaVersion = "1.0.0",
                invocationId = invocation.Id,
                conversationId = invocation.ConversationId,
                attemptId = attempt.Id,
                attemptNumber = attempt.AttemptNumber,
                fencingToken = AgentExecutionSupport.FencingTokenText(attempt.FencingToken),
                leaseExpiresAt = attempt.LeaseExpiresAt.UtcDateTime,
                leaseSeconds = policy.LeaseSeconds,
                deadlineAt = attempt.DeadlineAt.UtcDateTime,
                correlationId = invocation.CorrelationId,
                route = AgentPersistedNames.ToPersistedName(invocation.Route),
                agentId = invocation.AgentId,
                question = invocation.Question,
                history = history
                    .Where(t => t.Advice is not null)
                    .Select(t => new { invocationId = t.InvocationId, agentId = t.AgentId, question = t.Question, advice = t.Advice!.Advice })
                    .ToList(),
                scope = new { teamId = invocation.TeamId, matchId = invocation.MatchId, analysisRunId = invocation.AnalysisRunId },
                limits = new { maxAdviceCharacters = policy.MaxAdviceCharacters, maxCheckpointNoteCharacters = policy.MaxCheckpointNoteCharacters },
            });
        var validation = validator.Validate(ContractSchemaIds.AgentExecutionEnvelopeV1, envelope);
        if (!validation.IsValid)
        {
            throw new InvalidOperationException("The execution envelope does not conform to execution-envelope/v1.");
        }

        return envelope;
    }
}
