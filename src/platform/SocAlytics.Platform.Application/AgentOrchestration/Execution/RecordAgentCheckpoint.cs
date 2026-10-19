using System.Text.Json;
using Microsoft.Extensions.Options;
using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.AgentOrchestration.Workloads;
using SocAlytics.Platform.Domain.AgentOrchestration;

namespace SocAlytics.Platform.Application.AgentOrchestration.Execution;

public sealed record RecordAgentCheckpointCommand(Guid AttemptId, string WorkloadClientId, JsonElement Body);

public sealed record AgentCheckpointResult(Guid AttemptId, int Sequence, bool Replayed);

public sealed class RecordAgentCheckpointHandler(
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
    public async Task<OperationResult<AgentCheckpointResult>> HandleAsync(RecordAgentCheckpointCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var body = command.Body;
        var validation = validator.Validate(ContractSchemaIds.AgentAttemptReportV1, body);
        if (!validation.IsValid)
        {
            return OperationFailure.Validation(validation.Errors.Select(e => new FieldViolation(e.Field, e.Keyword)).ToArray());
        }

        if (!body.TryGetProperty("kind", out var kindElement) || !body.TryGetProperty("note", out var noteElement))
        {
            return OperationFailure.Validation(new FieldViolation("/kind", "required"));
        }

        var kind = kindElement.GetString()!;
        var note = noteElement.GetString()!;
        var operationId = body.GetProperty("operationId").GetString()!;
        var presentedToken = body.GetProperty("fencingToken").GetString()!;
        var requestHash = AgentExecutionSupport.Hash(
            $"{AgentWorkloadOperationKinds.Checkpoint}|{command.AttemptId:D}|{canonicalJson.Fingerprint(body)}");

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
            if (recorded.RequestHash != requestHash || recorded.Kind != AgentWorkloadOperationKinds.Checkpoint)
            {
                return OperationFailure.Conflict(AgentExecutionCodes.OperationIdReused);
            }

            using var outcome = JsonDocument.Parse(recorded.Outcome);
            return new AgentCheckpointResult(attempt.Id, outcome.RootElement.GetProperty("sequence").GetInt32(), true);
        }

        var policy = AgentExecutionSupport.ResolvePolicy(invocation.ExecutionPolicyJson, options.CurrentValue);
        if (note.Length < 1 || note.Length > policy.MaxCheckpointNoteCharacters)
        {
            return OperationFailure.Validation(new FieldViolation("/note", note.Length < 1 ? "minLength" : "maxLength"));
        }

        var now = time.GetUtcNow();
        if (AgentAttemptGuard.StaleDisposition(invocation, attempt, presentedToken, now) is { } disposition)
        {
            return AgentExecutionSupport.Stale(disposition);
        }

        if (await AgentScopeLoss.EnforceAsync(scopes, invocations, attempts, audit, invocation, attempt, now, cancellationToken))
        {
            await scope.CommitAsync(cancellationToken);
            return AgentExecutionSupport.Stale(AgentStaleDisposition.ScopeLost);
        }

        var sequence = await invocations.AppendLineageAsync(
            new NewAgentLineageEntry(
                invocation.Id, "checkpoint", attempt.Id, JsonSerializer.Serialize(new { kind, note }), now),
            cancellationToken);
        await invocations.TouchProgressAsync(invocation.Id, cancellationToken);
        await operations.InsertAsync(
            new AgentWorkloadOperationRecord(
                command.WorkloadClientId, operationId, AgentWorkloadOperationKinds.Checkpoint, requestHash, invocation.Id, attempt.Id,
                JsonSerializer.Serialize(new { attemptId = attempt.Id, sequence }), now),
            cancellationToken);
        await scope.CommitAsync(cancellationToken);
        return new AgentCheckpointResult(attempt.Id, sequence, false);
    }
}
