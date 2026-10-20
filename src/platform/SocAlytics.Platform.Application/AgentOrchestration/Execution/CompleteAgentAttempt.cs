using System.Text.Json;
using Microsoft.Extensions.Options;
using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.AgentOrchestration.Workloads;
using SocAlytics.Platform.Application.Telemetry;
using SocAlytics.Platform.Domain.AgentOrchestration;

namespace SocAlytics.Platform.Application.AgentOrchestration.Execution;

public sealed record CompleteAgentAttemptCommand(Guid AttemptId, string WorkloadClientId, JsonElement Body);

public sealed record AgentCompletionResult(Guid AttemptId, Guid InvocationId, string InvocationState, bool Replayed);

public sealed class CompleteAgentAttemptHandler(
    IUnitOfWork unitOfWork,
    IAgentInvocationStore invocations,
    IAgentAttemptStore attempts,
    IAgentWorkloadOperationStore operations,
    IContractValidator validator,
    ICanonicalJson canonicalJson,
    IAuditTrail audit,
    IInvocationScopeEvaluator scopes,
    IOptionsMonitor<AgentOrchestrationOptions> options,
    TimeProvider time)
{
    public async Task<OperationResult<AgentCompletionResult>> HandleAsync(CompleteAgentAttemptCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var body = command.Body;
        var validation = validator.Validate(ContractSchemaIds.AgentAttemptReportV1, body);
        if (!validation.IsValid)
        {
            return OperationFailure.Validation(validation.Errors.Select(e => new FieldViolation(e.Field, e.Keyword)).ToArray());
        }

        if (!body.TryGetProperty("advice", out var adviceElement) || !body.TryGetProperty("executionReferences", out var references))
        {
            return OperationFailure.Validation(new FieldViolation("/advice", "required"));
        }

        var advice = adviceElement.GetString()!;
        var operationId = body.GetProperty("operationId").GetString()!;
        var presentedToken = body.GetProperty("fencingToken").GetString()!;
        var requestHash = AgentExecutionSupport.Hash(
            $"{AgentWorkloadOperationKinds.Complete}|{command.AttemptId:D}|{canonicalJson.Fingerprint(body)}");

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
            if (recorded.RequestHash != requestHash || recorded.Kind != AgentWorkloadOperationKinds.Complete)
            {
                return OperationFailure.Conflict(AgentExecutionCodes.OperationIdReused);
            }

            AgentOrchestrationMetrics.CompletionRecorded("replayed");
            return new AgentCompletionResult(
                attempt.Id, invocation.Id, AgentPersistedNames.ToPersistedName(AgentInvocationState.Succeeded), true);
        }

        var policy = AgentExecutionSupport.ResolvePolicy(invocation.ExecutionPolicyJson, options.CurrentValue);
        if (advice.Length < 1 || advice.Length > policy.MaxAdviceCharacters)
        {
            return OperationFailure.Validation(new FieldViolation("/advice", advice.Length < 1 ? "minLength" : "maxLength"));
        }

        var now = time.GetUtcNow();
        var stale = AgentAttemptGuard.StaleDisposition(invocation, attempt, presentedToken, now);
        if (stale is { } disposition)
        {
            AgentOrchestrationMetrics.CompletionRecorded("stale");
            return AgentExecutionSupport.Stale(disposition);
        }

        if (await AgentScopeLoss.EnforceAsync(scopes, invocations, attempts, audit, invocation, attempt, now, cancellationToken))
        {
            await scope.CommitAsync(cancellationToken);
            AgentOrchestrationMetrics.CompletionRecorded("scope-lost");
            return AgentExecutionSupport.Stale(AgentStaleDisposition.ScopeLost);
        }

        await invocations.InsertAdviceAsync(
            new NewAgentAcceptedAdvice(invocation.Id, attempt.Id, advice, references.GetRawText(), now), cancellationToken);
        if (!await attempts.TryTransitionAsync(attempt.Id, AgentAttemptState.Active, AgentAttemptState.Completed, "completed", now, cancellationToken))
        {
            return AgentExecutionSupport.Stale(AgentStaleDisposition.AttemptNotCurrent);
        }

        var moved = await invocations.TransitionAsync(
            invocation.Id, AgentInvocationState.Running, invocation.Version, AgentInvocationState.Succeeded, null, now, cancellationToken);
        if (moved.Outcome != VersionedWriteOutcome.Applied)
        {
            throw new InvalidOperationException("The locked invocation could not be moved to succeeded.");
        }

        await invocations.SetCurrentAttemptAsync(invocation.Id, null, invocation.AttemptsUsed, cancellationToken);
        await invocations.AppendLineageAsync(
            new NewAgentLineageEntry(invocation.Id, "completed", attempt.Id, references.GetRawText(), now), cancellationToken);
        var result = new AgentCompletionResult(
            attempt.Id, invocation.Id, AgentPersistedNames.ToPersistedName(AgentInvocationState.Succeeded), false);
        await operations.InsertAsync(
            new AgentWorkloadOperationRecord(
                command.WorkloadClientId, operationId, AgentWorkloadOperationKinds.Complete, requestHash, invocation.Id, attempt.Id,
                JsonSerializer.Serialize(new { attemptId = attempt.Id, invocationId = invocation.Id, invocationState = result.InvocationState }), now),
            cancellationToken);
        await audit.RecordAsync(
            AgentOrchestrationAuditEvents.AttemptCompleted(
                attempt.Id, invocation.Id, invocation.TeamId, attempt.AttemptNumber, invocation.AgentId, command.WorkloadClientId),
            cancellationToken);
        await scope.CommitAsync(cancellationToken);
        AgentOrchestrationMetrics.CompletionRecorded("accepted");
        return result;
    }
}
