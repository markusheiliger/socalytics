using System.Text.Json;
using Microsoft.Extensions.Options;
using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.AgentOrchestration.Workloads;
using SocAlytics.Platform.Application.Messaging;
using SocAlytics.Platform.Application.Registry.QueueCredentials;
using SocAlytics.Platform.Application.Telemetry;
using SocAlytics.Platform.Domain.AgentOrchestration;

namespace SocAlytics.Platform.Application.AgentOrchestration.Execution;

public sealed record FailAgentAttemptCommand(Guid AttemptId, string WorkloadClientId, JsonElement Body);

public sealed record AgentFailureResult(Guid AttemptId, Guid InvocationId, string InvocationState, bool Replayed);

public sealed class FailAgentAttemptHandler(
    IUnitOfWork unitOfWork,
    IAgentInvocationStore invocations,
    IAgentAttemptStore attempts,
    IAgentWorkloadOperationStore operations,
    IContractValidator validator,
    ICanonicalJson canonicalJson,
    IOutbox outbox,
    IQueueCredentialIssuer queue,
    IAuditTrail audit,
    IOptionsMonitor<AgentOrchestrationOptions> options,
    TimeProvider time)
{
    public async Task<OperationResult<AgentFailureResult>> HandleAsync(FailAgentAttemptCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var body = command.Body;
        var validation = validator.Validate(ContractSchemaIds.AgentAttemptReportV1, body);
        if (!validation.IsValid)
        {
            return OperationFailure.Validation(validation.Errors.Select(e => new FieldViolation(e.Field, e.Keyword)).ToArray());
        }

        if (!body.TryGetProperty("reasonCode", out var reasonElement) || !body.TryGetProperty("retryable", out var retryableElement)
            || retryableElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return OperationFailure.Validation(new FieldViolation("/reasonCode", "required"));
        }

        var reasonCode = reasonElement.GetString()!;
        var retryable = retryableElement.GetBoolean();
        var operationId = body.GetProperty("operationId").GetString()!;
        var presentedToken = body.GetProperty("fencingToken").GetString()!;
        var requestHash = AgentExecutionSupport.Hash(
            $"{AgentWorkloadOperationKinds.Fail}|{command.AttemptId:D}|{canonicalJson.Fingerprint(body)}");

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

        var recorded = await operations.FindAsync(command.WorkloadClientId, operationId, cancellationToken);
        if (recorded is not null)
        {
            if (recorded.RequestHash != requestHash || recorded.Kind != AgentWorkloadOperationKinds.Fail)
            {
                return OperationFailure.Conflict(AgentExecutionCodes.OperationIdReused);
            }

            AgentOrchestrationMetrics.FailureRecorded("replayed");
            return new AgentFailureResult(attempt.Id, invocation.Id, RecordedInvocationState(recorded.Outcome), true);
        }

        var now = time.GetUtcNow();
        var stale = StaleDisposition(invocation, attempt, presentedToken, now);
        if (stale is { } disposition)
        {
            AgentOrchestrationMetrics.FailureRecorded("stale");
            return AgentExecutionSupport.Stale(disposition);
        }

        var policy = AgentExecutionSupport.ResolvePolicy(invocation.ExecutionPolicyJson, options.CurrentValue);
        var endReason = retryable ? "failed-retryable" : "failed-permanent";
        if (!await attempts.TryTransitionAsync(attempt.Id, AgentAttemptState.Active, AgentAttemptState.Failed, endReason, now, cancellationToken))
        {
            return AgentExecutionSupport.Stale(AgentStaleDisposition.AttemptNotCurrent);
        }

        await invocations.AppendLineageAsync(
            new NewAgentLineageEntry(
                invocation.Id, "failed", attempt.Id,
                JsonSerializer.Serialize(new { attemptId = attempt.Id, reasonCode, retryable }), now),
            cancellationToken);

        var requeue = retryable && policy.HasAttemptsRemaining(invocation.AttemptsUsed);
        AgentInvocationState newState;
        if (requeue)
        {
            newState = AgentInvocationState.Queued;
            await MoveAsync(invocation, newState, null, null, now, cancellationToken);
            var occurrence = invocation.ReadyOccurrence + 1;
            await invocations.SetReadyOccurrenceAsync(invocation.Id, occurrence, cancellationToken);
            await EnqueueReadyAsync(invocation, occurrence, cancellationToken);
            AgentOrchestrationMetrics.FailureRecorded("retry-queued");
        }
        else
        {
            newState = AgentInvocationState.Failed;
            var reason = retryable ? AgentFailureReason.AttemptsExhausted : AgentFailureReason.ExecutionFailed;
            await MoveAsync(invocation, newState, reason, now, now, cancellationToken);
            if (retryable)
            {
                await invocations.AppendLineageAsync(
                    new NewAgentLineageEntry(
                        invocation.Id, "exhausted", null,
                        JsonSerializer.Serialize(new { attemptsUsed = invocation.AttemptsUsed }), now),
                    cancellationToken);
                await audit.RecordAsync(
                    AgentOrchestrationAuditEvents.InvocationExhausted(
                        invocation.Id, invocation.TeamId, invocation.AgentId, attempt.AttemptNumber,
                        AgentPersistedNames.ToPersistedName(AgentFailureReason.AttemptsExhausted)),
                    cancellationToken);
            }

            AgentOrchestrationMetrics.FailureRecorded(AgentPersistedNames.ToPersistedName(reason));
        }

        await invocations.SetCurrentAttemptAsync(invocation.Id, null, invocation.AttemptsUsed, cancellationToken);
        var result = new AgentFailureResult(attempt.Id, invocation.Id, AgentPersistedNames.ToPersistedName(newState), false);
        await operations.InsertAsync(
            new AgentWorkloadOperationRecord(
                command.WorkloadClientId, operationId, AgentWorkloadOperationKinds.Fail, requestHash, invocation.Id, attempt.Id,
                JsonSerializer.Serialize(new { attemptId = attempt.Id, invocationId = invocation.Id, invocationState = result.InvocationState }), now),
            cancellationToken);
        await audit.RecordAsync(
            AgentOrchestrationAuditEvents.AttemptFailed(
                attempt.Id, invocation.Id, invocation.TeamId, attempt.AttemptNumber, invocation.AgentId, command.WorkloadClientId, reasonCode),
            cancellationToken);
        await scope.CommitAsync(cancellationToken);
        return result;
    }

    private async Task MoveAsync(
        AgentInvocationRecord invocation, AgentInvocationState to, AgentFailureReason? reason, DateTimeOffset? endedAt,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        var moved = await invocations.TransitionAsync(
            invocation.Id, AgentInvocationState.Running, invocation.Version, to, reason, endedAt, cancellationToken);
        if (moved.Outcome != VersionedWriteOutcome.Applied)
        {
            throw new InvalidOperationException($"The locked invocation could not be moved to {AgentPersistedNames.ToPersistedName(to)} at {now:O}.");
        }
    }

    private async Task EnqueueReadyAsync(AgentInvocationRecord invocation, int occurrence, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToElement(new
        {
            schemaVersion = "1.0.0",
            invocationId = invocation.Id,
            route = AgentPersistedNames.ToPersistedName(invocation.Route),
            agentId = invocation.AgentId,
            readyOccurrence = occurrence,
            correlationId = invocation.CorrelationId,
        });
        if (!validator.Validate(ContractSchemaIds.AgentWorkNotificationV1, payload).IsValid)
        {
            throw new InvalidOperationException("The work notification does not conform to work-notification/v1.");
        }

        await outbox.EnqueueAsync(
            new OutboxMessageDraft(
                Guid.CreateVersion7(), PublishOutboxBatch.AgentInvocationReadyMessageType, "1.0.0", queue.AgentWorkSubject, payload, Guid.CreateVersion7()),
            cancellationToken);
    }

    private static string RecordedInvocationState(string outcome)
    {
        using var document = JsonDocument.Parse(outcome);
        return document.RootElement.GetProperty("invocationState").GetString()!;
    }

    private static AgentStaleDisposition? StaleDisposition(
        AgentInvocationRecord invocation, AgentAttemptRecord attempt, string presentedToken, DateTimeOffset now)
    {
        if (AgentStateTransitions.IsTerminal(invocation.State))
        {
            return AgentStaleDisposition.InvocationEnded;
        }

        if (attempt.State == AgentAttemptState.Fenced)
        {
            return AgentStaleDisposition.AttemptFenced;
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
