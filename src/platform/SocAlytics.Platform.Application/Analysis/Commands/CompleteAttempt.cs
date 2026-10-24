using System.Text.Json;
using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.Abstractions.ObjectStorage;
using SocAlytics.Platform.Application.Analysis.Results;
using SocAlytics.Platform.Application.Analysis.Stores;
using SocAlytics.Platform.Application.Telemetry;
using SocAlytics.Platform.Domain.Analysis;

namespace SocAlytics.Platform.Application.Analysis.Commands;

public sealed record CompleteAttemptCommand(Guid AttemptId, JsonElement Body);

public sealed record CompletionOutcomeResult(
    Guid AttemptId, Guid LogicalJobId, string Outcome, Guid? AcceptedResultId, bool Replayed, string LogicalJobState, string? Reason = null);

public sealed class CompleteAttemptHandler(
    IUnitOfWork unitOfWork,
    IContractValidator validator,
    ICanonicalJson canonicalJson,
    IAnalysisRunStore runs,
    ILogicalJobStore jobs,
    IExecutionAttemptStore attempts,
    IAcceptedResultStore results,
    ICompletionOutcomeStore outcomes,
    IAcceptedResultManifestStore manifests,
    IIndexedFactStore facts,
    ResultManifestVerifier verifier,
    IAuditTrail audit,
    RunReadinessCoordinator coordinator,
    ICurrentAnalystManager manager,
    TimeProvider time)
{
    public const string AttemptObsoleteCode = "attempt-obsolete";
    public const string CompletionRejectedCode = "completion-rejected";
    public const string IdempotencyKeyReusedCode = "idempotency-key-reused";

    public const string FailedOutcome = "failed";

    private sealed class PendingVerification
    {
        public ResultVerificationRequest? Request { get; set; }
    }

    public async Task<OperationResult<CompletionOutcomeResult>> HandleAsync(CompleteAttemptCommand command, CancellationToken cancellationToken)
    {
        var pending = new PendingVerification();
        var first = await RunAsync(command, null, pending, cancellationToken);
        if (first is not null)
        {
            return first;
        }

        // The manifest is verified outside the unit of work; the second pass re-checks everything under the locks.
        var verification = await verifier.VerifyAsync(pending.Request!, cancellationToken);
        if (verification.Status == ResultVerificationStatus.StorageUnavailable)
        {
            throw new ObjectStorageUnavailableException();
        }

        return await RunAsync(command, verification, pending, cancellationToken)
            ?? throw new InvalidOperationException("The completion was not decided.");
    }

    private async Task<OperationResult<CompletionOutcomeResult>?> RunAsync(
        CompleteAttemptCommand command, ResultVerificationOutcome? verification, PendingVerification pending, CancellationToken cancellationToken)
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
                stored.AttemptId, job.Id, stored.Outcome, stored.AcceptedResultId, true, job.State.ToString(), stored.Reason);
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
            AnalysisMetrics.CompletionRejected(AttemptObsoleteCode);
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
        AnalysisMetrics.TagActivity(run.Id, job.Id, attempt.Id, attempt.ManagerId, node.CapabilityId);
        if (!matches)
        {
            AnalysisMetrics.CompletionRejected(CompletionRejectedCode);
            await CloseAsync(attempt, ExecutionAttemptState.Rejected, "completion-rejected", job.Id, ReadinessCause.AttemptRejected, cancellationToken);
            await scope.CommitAsync(cancellationToken);
            return OperationFailure.Conflict(CompletionRejectedCode);
        }

        Guid? acceptedId = null;
        string outcome;
        string? reason = null;
        if (succeeded)
        {
            if (verification is null)
            {
                var upstream = (await results.ListByRunAsync(run.Id, cancellationToken)).Where(r => recordedUpstream.Contains(r.Id)).ToList();
                var result = body.GetProperty("result");
                var inputSnapshot = await jobs.LoadInputSnapshotAsync(job.Id, cancellationToken);
                var inputDependencies = ExpectedInputDependency.Build(
                    inputSnapshot, hierarchy.Nodes, await jobs.LoadByRunAsync(run.Id, cancellationToken), upstream);
                pending.Request = new ResultVerificationRequest(
                    run, hierarchy.Snapshot.SnapshotJson, node, job, snapshot, attempt.Id, attempt.AttemptNumber,
                    result.GetProperty("manifestReference").GetString()!, result.GetProperty("manifestChecksum").GetString()!, upstream,
                    inputDependencies);
                return null;
            }

            if (verification.Status == ResultVerificationStatus.Rejected)
            {
                reason = verification.Reason;
                AnalysisMetrics.CompletionRejected(ResultRejectionReasons.Code(reason!));
                await CloseAsync(attempt, ExecutionAttemptState.Failed, reason, job.Id, ReadinessCause.AttemptFailed, cancellationToken);
                await audit.RecordAsync(AnalysisAuditEvents.ResultRejected(attempt.Id, job.Id, run.Id, run.TeamId, manager.ManagerId, ResultRejectionReasons.Code(reason!)), cancellationToken);
                outcome = FailedOutcome;
            }
            else
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
                var validated = verification.Manifest!;
                acceptedId = Guid.CreateVersion7();
                await results.InsertAsync(
                    new AcceptedResultRecord(
                        acceptedId.Value, job.Id, run.Id, node.Id, attempt.Id,
                        result.GetProperty("manifestReference").GetString()!, result.GetProperty("manifestChecksum").GetString()!,
                        node.CapabilityId, node.CapabilityVersion, snapshot.SchemaVersion, snapshot.AnalystProfileDigest,
                        snapshot.ImageDigest, snapshot.ModelDigest, policies, body.GetProperty("executedRuntime").GetRawText(),
                        recordedUpstream, now),
                    cancellationToken);
                await manifests.InsertAsync(
                    new AcceptedResultManifestRecord(
                        acceptedId.Value, validated.ContractVersion, validated.OutputType, validated.ValidityStartMs, validated.ValidityEndMs,
                        validated.FactCount, validated.ArtifactsJson, validated.UpstreamLineageJson),
                    cancellationToken);
                if (validated.Facts.Count > 0)
                {
                    await facts.InsertAsync(
                        validated.Facts.Select((f, i) => new IndexedFactRecord(
                            acceptedId.Value, i, run.Id, node.Id, run.TeamId, run.MatchId, node.CapabilityId, node.CapabilityVersion,
                            snapshot.SchemaVersion, validated.OutputType, f.Kind, f.StartMs, f.EndMs, f.Confidence, f.PayloadJson)).ToList(),
                        cancellationToken);
                }

                if (!await jobs.TryTransitionAsync(job.Id, LogicalJobState.Claimed, LogicalJobState.Completed, null, null, cancellationToken))
                {
                    throw new InvalidOperationException("The logical job changed state concurrently.");
                }

                await coordinator.ApplyAsync(run.Id, cancellationToken);
                outcome = "accepted";
            }
        }
        else
        {
            await CloseAsync(attempt, ExecutionAttemptState.Failed, body.GetProperty("failure").GetProperty("category").GetString(), job.Id, ReadinessCause.AttemptFailed, cancellationToken);
            outcome = "failure-recorded";
        }

        await outcomes.InsertAsync(new CompletionOutcomeRecord(job.Id, key, attempt.Id, fingerprint, outcome, acceptedId, now, reason), cancellationToken);
        var after = await jobs.LockJobAsync(job.Id, cancellationToken);
        await scope.CommitAsync(cancellationToken);
        return new CompletionOutcomeResult(attempt.Id, job.Id, outcome, acceptedId, false, (after ?? job).State.ToString(), reason);
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
