using System.Text.Json;
using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.Analysis.Stores;
using SocAlytics.Platform.Domain.Analysis;

namespace SocAlytics.Platform.Application.Analysis.Commands;

public sealed record CompleteAttemptCommand(Guid AttemptId, JsonElement Body);

public sealed record CompletionOutcomeResult(
    Guid AttemptId, Guid LogicalJobId, string Outcome, Guid? AcceptedResultId, bool Replayed, string LogicalJobState);

public sealed class CompleteAttemptHandler(
    IUnitOfWork unitOfWork,
    IContractValidator validator,
    ICanonicalJson canonicalJson,
    IAnalysisRunStore runs,
    ILogicalJobStore jobs,
    IExecutionAttemptStore attempts,
    IAcceptedResultStore results,
    ICompletionOutcomeStore outcomes,
    RunReadinessCoordinator coordinator,
    ICurrentAnalystManager manager,
    TimeProvider time)
{
    public const string AttemptObsoleteCode = "attempt-obsolete";
    public const string CompletionRejectedCode = "completion-rejected";
    public const string IdempotencyKeyReusedCode = "idempotency-key-reused";

    public async Task<OperationResult<CompletionOutcomeResult>> HandleAsync(CompleteAttemptCommand command, CancellationToken cancellationToken)
    {
        var body = command.Body;
        var validation = validator.Validate(ContractSchemaIds.AttemptCompletionV1, body);
        if (!validation.IsValid)
        {
            return OperationFailure.Validation(validation.Errors.Select(e => new FieldViolation(e.Field, e.Keyword)).ToArray());
        }

        if (body.GetProperty("attemptId").GetGuid() != command.AttemptId)
        {
            return OperationFailure.Validation(new FieldViolation("/attemptId", "const"));
        }

        var jobId = body.GetProperty("logicalJobId").GetGuid();
        var runId = body.GetProperty("runId").GetGuid();
        var key = body.GetProperty("idempotencyKey").GetString()!;
        var fingerprint = canonicalJson.Fingerprint(body);

        await using var scope = await unitOfWork.BeginAsync(cancellationToken);
        var run = await runs.LockRunAsync(runId, cancellationToken);
        var job = await jobs.LockJobAsync(jobId, cancellationToken);
        var attempt = await attempts.LockAsync(command.AttemptId, cancellationToken);
        if (run is null || job is null || attempt is null || job.RunId != run.Id || attempt.LogicalJobId != job.Id
            || attempt.ManagerId != manager.ManagerId)
        {
            return OperationFailure.Conflict(AttemptObsoleteCode);
        }

        var stored = await outcomes.GetAsync(job.Id, key, cancellationToken);
        if (stored is not null)
        {
            if (stored.RequestFingerprint != fingerprint)
            {
                return OperationFailure.Conflict(IdempotencyKeyReusedCode);
            }

            return new CompletionOutcomeResult(
                stored.AttemptId, job.Id, stored.Outcome, stored.AcceptedResultId, true, job.State.ToString());
        }

        var now = time.GetUtcNow();
        if (attempt.State != ExecutionAttemptState.Active
            || attempt.LeaseExpiresAt <= now
            || job.State != LogicalJobState.Claimed
            || job.CurrentAttemptId != attempt.Id
            || attempt.FencingToken != body.GetProperty("fencingToken").GetInt64()
            || attempt.AttemptNumber != body.GetProperty("attemptNumber").GetInt32()
            || await results.GetByJobAsync(job.Id, cancellationToken) is not null)
        {
            return OperationFailure.Conflict(AttemptObsoleteCode);
        }

        var hierarchy = await runs.LoadAsync(run.Id, cancellationToken)
            ?? throw new InvalidOperationException("The analysis run does not exist.");
        var node = hierarchy.Nodes.Single(n => n.Id == job.NodeId);
        var snapshot = await jobs.LoadSnapshotAsync(job.Id, cancellationToken)
            ?? throw new InvalidOperationException("The Logical Job has no snapshot.");
        var recordedUpstream = await LoadUpstreamAsync(job.Id, cancellationToken);
        var claimedUpstream = body.GetProperty("acceptedUpstreamResultIds").EnumerateArray().Select(x => x.GetGuid()).ToList();
        var capability = body.GetProperty("capability");
        var model = body.GetProperty("executedModelDigest");

        var matches = body.GetProperty("teamId").GetGuid() == run.TeamId
            && body.GetProperty("matchId").GetGuid() == run.MatchId
            && body.GetProperty("nodeId").GetGuid() == node.Id
            && capability.GetProperty("id").GetString() == node.CapabilityId
            && Pad(capability.GetProperty("version").GetString()) == Pad(node.CapabilityVersion)
            && Pad(body.GetProperty("schemaVersion").GetString()) == Pad(snapshot.SchemaVersion)
            && body.GetProperty("executedImageDigest").GetString() == snapshot.ImageDigest
            && (model.ValueKind == JsonValueKind.Null ? null : model.GetString()) == snapshot.ModelDigest
            && claimedUpstream.Order().SequenceEqual(recordedUpstream.Order());

        var succeeded = body.GetProperty("outcome").GetString() == "succeeded";
        if (!matches)
        {
            await CloseAsync(attempt, ExecutionAttemptState.Rejected, "completion-rejected", job.Id, ReadinessCause.AttemptRejected, cancellationToken);
            await scope.CommitAsync(cancellationToken);
            return OperationFailure.Conflict(CompletionRejectedCode);
        }

        Guid? acceptedId = null;
        string outcome;
        if (succeeded)
        {
            if (!await attempts.TryCloseAsync(attempt.Id, ExecutionAttemptState.Active, ExecutionAttemptState.Accepted, null, cancellationToken))
            {
                return OperationFailure.Conflict(AttemptObsoleteCode);
            }

            using var nodeDoc = JsonDocument.Parse(node.SnapshotJson);
            var policies = nodeDoc.RootElement.TryGetProperty("policyVersions", out var pv) && pv.ValueKind == JsonValueKind.Object
                ? pv.GetRawText()
                : "{}";
            var result = body.GetProperty("result");
            acceptedId = Guid.CreateVersion7();
            await results.InsertAsync(
                new AcceptedResultRecord(
                    acceptedId.Value, job.Id, run.Id, node.Id, attempt.Id,
                    result.GetProperty("manifestReference").GetString()!, result.GetProperty("manifestChecksum").GetString()!,
                    node.CapabilityId, node.CapabilityVersion, snapshot.SchemaVersion, snapshot.AnalystProfileDigest,
                    snapshot.ImageDigest, snapshot.ModelDigest, policies, body.GetProperty("executedRuntime").GetRawText(),
                    recordedUpstream, now),
                cancellationToken);
            if (!await jobs.TryTransitionAsync(job.Id, LogicalJobState.Claimed, LogicalJobState.Completed, null, null, cancellationToken))
            {
                throw new InvalidOperationException("The logical job changed state concurrently.");
            }

            await coordinator.ApplyAsync(run.Id, cancellationToken);
            outcome = "accepted";
        }
        else
        {
            await CloseAsync(attempt, ExecutionAttemptState.Failed, body.GetProperty("failure").GetProperty("category").GetString(), job.Id, ReadinessCause.AttemptFailed, cancellationToken);
            outcome = "failure-recorded";
        }

        await outcomes.InsertAsync(new CompletionOutcomeRecord(job.Id, key, attempt.Id, fingerprint, outcome, acceptedId, now), cancellationToken);
        var after = await jobs.LockJobAsync(job.Id, cancellationToken);
        await scope.CommitAsync(cancellationToken);
        return new CompletionOutcomeResult(attempt.Id, job.Id, outcome, acceptedId, false, (after ?? job).State.ToString());
    }

    private async Task CloseAsync(
        ExecutionAttemptRecord attempt, ExecutionAttemptState state, string? reason, Guid jobId, ReadinessCause cause, CancellationToken ct)
    {
        if (!await attempts.TryCloseAsync(attempt.Id, ExecutionAttemptState.Active, state, reason, ct))
        {
            throw new InvalidOperationException("The attempt changed state concurrently.");
        }

        await coordinator.RequeueOrFailAsync(jobId, cause, ct);
    }

    private async Task<List<Guid>> LoadUpstreamAsync(Guid jobId, CancellationToken ct)
    {
        var ids = new List<Guid>();
        if (await jobs.LoadInputsAsync(jobId, ct) is { } json)
        {
            using var doc = JsonDocument.Parse(json);
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                ids.AddRange(e.GetProperty("acceptedResultIds").EnumerateArray().Select(x => x.GetGuid()));
            }
        }

        return ids;
    }

    private static string Pad(string? value)
    {
        var parts = (value ?? "0").Split('.');
        return string.Join('.', parts.Concat(Enumerable.Repeat("0", Math.Max(0, 3 - parts.Length))).Take(3));
    }
}
