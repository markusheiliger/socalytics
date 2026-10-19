using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Analysis.Stores;
using SocAlytics.Platform.Domain.Analysis;
using SocAlytics.Platform.Domain.Recordings;

namespace SocAlytics.Platform.Application.Analysis;

public sealed record ReadinessApplication(int ReadyJobs, int RunTransitions);

public enum ReadinessCause
{
    AttemptStale,
    AttemptFailed,
    AttemptRejected,
}

// Applies readiness inside the caller's unit of work; the caller has already locked the run row.
public sealed class RunReadinessCoordinator(
    IAnalysisRunStore runs,
    ILogicalJobStore jobs,
    IAcceptedResultStore results,
    IOutbox outbox,
    IContractValidator validator,
    TimeProvider time,
    ILogger<RunReadinessCoordinator>? logger = null)
{
    private const string ContractVersion = "1.0.0";

    public async Task<ReadinessApplication> ApplyAsync(Guid runId, CancellationToken cancellationToken)
    {
        var hierarchy = await runs.LoadAsync(runId, cancellationToken)
            ?? throw new InvalidOperationException("The analysis run does not exist.");
        var run = hierarchy.Run;
        var runTransitions = 0;

        if (run.State == AnalysisRunState.Requested)
        {
            if (!await runs.TryTransitionAsync(runId, AnalysisRunState.Requested, AnalysisRunState.Running, null, cancellationToken))
            {
                throw new InvalidOperationException("The run left the Requested state concurrently.");
            }

            await EnqueueRunStateAsync(run, AnalysisRunState.Requested, AnalysisRunState.Running, null, cancellationToken);
            runTransitions++;
            run = run with { State = AnalysisRunState.Running };
        }

        var jobRecords = await jobs.LoadByRunAsync(runId, cancellationToken);
        var accepted = await results.ListByRunAsync(runId, cancellationToken);
        var acceptedById = accepted.ToDictionary(a => a.Id);
        var nodesByKey = hierarchy.Nodes.ToDictionary(n => n.NodeKey, StringComparer.Ordinal);
        var nodesById = hierarchy.Nodes.ToDictionary(n => n.Id);
        var jobsById = jobRecords.ToDictionary(j => j.Id);

        var state = new RunGraphState(
            run.State,
            run.RequestedCapabilities,
            hierarchy.Nodes.Select(n => new RunNodeState(n.NodeKey, n.CapabilityId, n.ExecutionScope, n.State)).ToList(),
            hierarchy.Edges.Select(e => new RunEdgeState(
                nodesById[e.UpstreamNodeId].NodeKey,
                nodesById[e.DependentNodeId].NodeKey,
                e.Kind,
                e.PredicateJson is not null && ConditionalPredicate.TryParseJson(e.PredicateJson, out var p) ? p : null)
            {
                CoveragePolicy = e.CoveragePolicy,
            }).ToList(),
            jobRecords.Select(j => new RunJobState(
                j.Id,
                nodesById[j.NodeId].NodeKey,
                ToSegment(j),
                j.State,
                j.State != LogicalJobState.Waiting,
                accepted.Where(a => a.LogicalJobId == j.Id).Select(a => a.Id).ToList())).ToList());

        var evaluation = ReadinessEvaluator.Evaluate(state);

        foreach (var transition in evaluation.NodeTransitions)
        {
            var node = nodesByKey[transition.NodeKey];
            Guid? blocker = transition.BlockingNodeKey is { } key ? nodesByKey[key].Id : null;
            if (!await runs.TryTransitionNodeAsync(node.Id, transition.From, transition.To, blocker, cancellationToken))
            {
                throw new InvalidOperationException("A workflow node changed state concurrently.");
            }
        }

        foreach (var blocked in evaluation.BlockedJobs)
        {
            var previous = jobsById[blocked.JobId].State;
            Telemetry.AnalysisMetrics.RunBlocked(null);
            var reason = blocked.BelowMinimumCoverage ? AnalysisPersistedNames.UpstreamCoverageBelowMinimum : "upstream-required-failed";
            if (await jobs.TryTransitionAsync(blocked.JobId, previous, LogicalJobState.Blocked, reason, null, cancellationToken))
            {
                if (blocked.BelowMinimumCoverage)
                {
                    Telemetry.AnalysisMetrics.PartialBarrierBlocked();
                    logger?.LogWarning("Analysis job {LogicalJobId} of run {RunId} was blocked: {Reason}.", blocked.JobId, runId, reason);
                }

                if (previous == LogicalJobState.Ready)
                {
                    await outbox.DiscardUnpublishedReadyWorkAsync(blocked.JobId, cancellationToken);
                }
            }
        }

        var ready = 0;
        foreach (var readyJob in evaluation.ReadyJobs)
        {
            var job = await jobs.LockJobAsync(readyJob.JobId, cancellationToken)
                ?? throw new InvalidOperationException("The logical job does not exist.");
            if (job.State != LogicalJobState.Waiting)
            {
                continue;
            }

            var inputs = readyJob.Snapshot ?? [];
            if (await jobs.TryInsertInputSnapshotAsync(job.Id, inputs, cancellationToken))
            {
                foreach (var barrier in inputs.Where(i => i.CoverageMode == CoverageMode.Partial))
                {
                    var coverage = barrier.Status == SnapshotStatus.Accepted ? "complete" : "partial";
                    Telemetry.AnalysisMetrics.PartialBarrierReady(coverage);
                    logger?.LogInformation("Analysis job {LogicalJobId} of run {RunId} became ready behind a partial barrier with {Coverage} coverage.", job.Id, runId, coverage);
                }
            }

            var occurrenceId = Guid.CreateVersion7();
            var sequence = await jobs.InsertReadinessOccurrenceAsync(job.Id, occurrenceId, "initial", cancellationToken);
            await jobs.SetCurrentOccurrenceAsync(job.Id, occurrenceId, null, cancellationToken);
            if (!await jobs.TryTransitionAsync(job.Id, LogicalJobState.Waiting, LogicalJobState.Ready, null, null, cancellationToken))
            {
                throw new InvalidOperationException("The logical job changed state concurrently.");
            }

            var node = nodesById[job.NodeId];
            var snapshot = await jobs.LoadSnapshotAsync(job.Id, cancellationToken)
                ?? throw new InvalidOperationException("The logical job snapshot is missing.");
            var messageId = Guid.CreateVersion7();
            var payload = BuildJobPayload(run, node, job, snapshot, occurrenceId, sequence, messageId, inputs, nodesByKey, acceptedById);
            Validate(ContractSchemaIds.AnalystJobV1, payload);
            await outbox.EnqueueAsync(new OutboxMessageDraft(
                messageId, OutboxMessageTypes.AnalysisReadyWork, ContractVersion,
                $"analysis.jobs.ready.{node.CapabilityId}", payload, occurrenceId, run.Id, job.Id, node.CapabilityId), cancellationToken);
            ready++;
        }

        if (evaluation.RunOutcome is { } outcome && outcome != run.State)
        {
            var reason = outcome == AnalysisRunState.Failed ? "no-required-result" : null;
            if (await runs.TryTransitionAsync(runId, run.State, outcome, reason, cancellationToken))
            {
                await EnqueueRunStateAsync(run, run.State, outcome, reason, cancellationToken);
                runTransitions++;
            }
        }

        return new ReadinessApplication(ready, runTransitions);
    }

    // Called inside the caller's unit of work after the run and job rows are locked and the attempt was closed.
    public async Task RequeueOrFailAsync(Guid logicalJobId, ReadinessCause cause, CancellationToken cancellationToken)
    {
        var job = await jobs.LockJobAsync(logicalJobId, cancellationToken)
            ?? throw new InvalidOperationException("The logical job does not exist.");
        if (job.State != LogicalJobState.Claimed)
        {
            return;
        }

        var snapshot = await jobs.LoadSnapshotAsync(job.Id, cancellationToken)
            ?? throw new InvalidOperationException("The logical job snapshot is missing.");

        if (job.AttemptsUsed >= snapshot.MaxAttempts)
        {
            await jobs.SetCurrentOccurrenceAsync(job.Id, job.CurrentReadinessOccurrenceId, null, cancellationToken);
            if (!await jobs.TryTransitionAsync(job.Id, LogicalJobState.Claimed, LogicalJobState.Failed, "attempts-exhausted", null, cancellationToken))
            {
                throw new InvalidOperationException("The logical job changed state concurrently.");
            }

            await ApplyAsync(job.RunId, cancellationToken);
            return;
        }

        var hierarchy = await runs.LoadAsync(job.RunId, cancellationToken)
            ?? throw new InvalidOperationException("The analysis run does not exist.");
        var nodesByKey = hierarchy.Nodes.ToDictionary(n => n.NodeKey, StringComparer.Ordinal);
        var node = hierarchy.Nodes.Single(n => n.Id == job.NodeId);
        var accepted = (await results.ListByRunAsync(job.RunId, cancellationToken)).ToDictionary(a => a.Id);

        var inputs = await jobs.LoadInputSnapshotAsync(job.Id, cancellationToken);

        var persisted = cause switch
        {
            ReadinessCause.AttemptStale => "attempt-stale",
            ReadinessCause.AttemptFailed => "attempt-failed",
            _ => "attempt-rejected",
        };
        var occurrenceId = Guid.CreateVersion7();
        var sequence = await jobs.InsertReadinessOccurrenceAsync(job.Id, occurrenceId, persisted, cancellationToken);
        await jobs.SetCurrentOccurrenceAsync(job.Id, occurrenceId, null, cancellationToken);
        if (!await jobs.TryTransitionAsync(job.Id, LogicalJobState.Claimed, LogicalJobState.Ready, null, null, cancellationToken))
        {
            throw new InvalidOperationException("The logical job changed state concurrently.");
        }

        var messageId = Guid.CreateVersion7();
        var payload = BuildJobPayload(hierarchy.Run, node, job, snapshot, occurrenceId, sequence, messageId, inputs, nodesByKey, accepted);
        Validate(ContractSchemaIds.AnalystJobV1, payload);
        await outbox.EnqueueAsync(new OutboxMessageDraft(
            messageId, OutboxMessageTypes.AnalysisReadyWork, ContractVersion,
            $"analysis.jobs.ready.{node.CapabilityId}", payload, occurrenceId, hierarchy.Run.Id, job.Id, node.CapabilityId), cancellationToken);
    }

    private static AnalysisSegmentReference? ToSegment(LogicalJobRecord j) =>
        j.SegmentNumber is { } n
        && Sha256Digest.TryParse(j.TimelineMappingDigest, out var mapping)
        && Sha256Digest.TryParse(j.SegmentationPolicyDigest, out var policy) && j.RecordingVersionId is { } rv
            ? new AnalysisSegmentReference(rv, mapping, policy, n)
            : null;

    private void Validate(string schemaId, JsonElement payload)
    {
        var result = validator.Validate(schemaId, payload);
        if (!result.IsValid)
        {
            throw new InvalidOperationException(
                "A generated analysis message violates its contract: " + string.Join(", ", result.Errors.Select(e => $"{e.Field}:{e.Keyword}")));
        }
    }

    private string Now() => time.GetUtcNow().UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private async Task EnqueueRunStateAsync(AnalysisRunRecord run, AnalysisRunState from, AnalysisRunState to, string? reason, CancellationToken ct)
    {
        var messageId = Guid.CreateVersion7();
        var transitionId = Guid.CreateVersion7();
        var payload = new Dictionary<string, object?>
        {
            ["contractVersion"] = ContractVersion,
            ["messageId"] = messageId,
            ["occurredAt"] = Now(),
            ["runId"] = run.Id,
            ["teamId"] = run.TeamId,
            ["matchId"] = run.MatchId,
            ["recordingSetVersionId"] = run.RecordingSetVersionId,
            ["workflow"] = new { id = run.WorkflowId, version = Semver(run.WorkflowVersion) },
            ["previousState"] = from.ToString(),
            ["state"] = to.ToString(),
            ["correlation"] = new { correlationId = run.Id, causationId = transitionId },
        };
        if (reason is not null)
        {
            payload["failureReason"] = reason;
        }

        var element = JsonSerializer.SerializeToElement(payload);
        Validate(ContractSchemaIds.RunStateChangedV1, element);
        await outbox.EnqueueAsync(new OutboxMessageDraft(
            messageId, OutboxMessageTypes.AnalysisRunStateChanged, ContractVersion,
            "analysis.events.run-state-changed", element, transitionId, run.Id), ct);
    }

    private JsonElement BuildJobPayload(
        AnalysisRunRecord run, WorkflowNodeRecord node, LogicalJobRecord job, LogicalJobSnapshotRecord snapshot,
        Guid occurrenceId, int sequence, Guid messageId, IReadOnlyList<InputSnapshotEntry> inputs,
        Dictionary<string, WorkflowNodeRecord> nodesByKey, Dictionary<Guid, AcceptedResultRecord> acceptedById)
    {
        using var nodeDoc = JsonDocument.Parse(node.SnapshotJson);
        var profileId = nodeDoc.RootElement.GetProperty("analystProfileId").GetString();
        var policies = nodeDoc.RootElement.TryGetProperty("policyVersions", out var pv) && pv.ValueKind == JsonValueKind.Object
            ? pv.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal)
                .Select(p => new { policyId = p.Name, version = Semver(p.Value.GetString()) }).ToList()
            : [];
        var resources = JsonSerializer.Deserialize<Dictionary<string, string>>(snapshot.ResourcesJson) ?? [];

        var payload = new Dictionary<string, object?>
        {
            ["contractVersion"] = ContractVersion,
            ["messageId"] = messageId,
            ["readinessOccurrenceId"] = occurrenceId,
            ["readinessSequence"] = sequence,
            ["runId"] = run.Id,
            ["nodeId"] = node.Id,
            ["nodeKey"] = node.NodeKey,
            ["logicalJobId"] = job.Id,
            ["teamId"] = run.TeamId,
            ["matchId"] = run.MatchId,
            ["recordingSetVersionId"] = run.RecordingSetVersionId,
            ["capability"] = new { id = node.CapabilityId, version = Semver(node.CapabilityVersion) },
            ["tier"] = AnalysisPersistedNames.ToPersistedName(node.Tier),
            ["schemaVersion"] = Semver(snapshot.SchemaVersion),
            ["executionScope"] = AnalysisPersistedNames.ToPersistedName(node.ExecutionScope),
            ["inputs"] = inputs.Select(i =>
            {
                var upstream = nodesByKey[i.UpstreamNodeKey];
                return new
                {
                    upstreamNodeKey = i.UpstreamNodeKey,
                    capability = new { id = upstream.CapabilityId, version = Semver(upstream.CapabilityVersion) },
                    edgeKind = AnalysisPersistedNames.ToPersistedName(i.EdgeKind),
                    // The job contract has no partial status; accepted results are the consumed ones.
                    status = i.Status == SnapshotStatus.Partial ? SnapshotStatus.Accepted : i.Status,
                    acceptedResults = i.AcceptedResultIds.Select(id => acceptedById[id]).Select(a => new
                    {
                        acceptedResultId = a.Id,
                        logicalJobId = a.LogicalJobId,
                        capability = new { id = a.CapabilityId, version = Semver(a.CapabilityVersion) },
                        schemaVersion = Semver(a.SchemaVersion),
                        manifestReference = a.ManifestReference,
                        manifestChecksum = a.ManifestChecksum,
                    }).ToList(),
                };
            }).ToList(),
            ["pinned"] = new
            {
                analystProfileId = profileId,
                analystProfileDigest = snapshot.AnalystProfileDigest,
                imageDigest = snapshot.ImageDigest,
                modelDigest = snapshot.ModelDigest,
                policies,
            },
            ["resources"] = new
            {
                cpuMillicores = ParseCpu(resources.GetValueOrDefault("cpu")),
                memoryMebibytes = ParseMebibytes(resources.GetValueOrDefault("memory"), 1),
                scratchDiskMebibytes = ParseMebibytes(resources.GetValueOrDefault("scratch"), 0),
            },
            ["attemptBudget"] = new { maxAttempts = snapshot.MaxAttempts },
            ["timeouts"] = new
            {
                leaseSeconds = snapshot.LeaseSeconds,
                staleTimeoutSeconds = snapshot.StaleTimeoutSeconds,
                executionTimeoutSeconds = snapshot.ExecutionTimeoutSeconds,
            },
            ["createdAt"] = Now(),
            ["correlation"] = new { correlationId = run.Id, causationId = occurrenceId },
        };

        if (node.ExecutionScope == ExecutionScope.Segment)
        {
            payload["segment"] = new
            {
                recordingSetVersionId = run.RecordingSetVersionId,
                recordingVersionId = job.RecordingVersionId,
                timelineMappingDigest = job.TimelineMappingDigest,
                segmentationPolicyDigest = job.SegmentationPolicyDigest,
                segmentNumber = job.SegmentNumber,
            };
        }

        return JsonSerializer.SerializeToElement(payload);
    }

    // Pads short versions such as "1" or "1.2" to the semantic-version form the contracts require.
    private static string Semver(string? value)
    {
        var parts = (value ?? "0").Split('.');
        return string.Join('.', parts.Concat(Enumerable.Repeat("0", Math.Max(0, 3 - parts.Length))).Take(3));
    }

    private static int ParseCpu(string? value)
    {
        if (value is null)
        {
            return 1000;
        }

        if (value.EndsWith('m') && int.TryParse(value[..^1], out var milli))
        {
            return Math.Max(1, milli);
        }

        return double.TryParse(value, CultureInfo.InvariantCulture, out var cores) ? Math.Max(1, (int)(cores * 1000)) : 1000;
    }

    private static int ParseMebibytes(string? value, int minimum)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return minimum == 0 ? 0 : 1024;
        }

        var digits = new string(value.TakeWhile(char.IsDigit).ToArray());
        if (!long.TryParse(digits, out var number))
        {
            return minimum;
        }

        var mebibytes = value[digits.Length..] switch
        {
            "Gi" or "G" => number * 1024,
            "Ti" => number * 1024 * 1024,
            "Ki" => number / 1024,
            _ => number,
        };
        return (int)Math.Max(minimum, Math.Min(mebibytes, int.MaxValue));
    }
}
