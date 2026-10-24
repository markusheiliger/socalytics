using System.Text.Json;
using SocAlytics.Platform.Application.Analysis.Commands;
using SocAlytics.Platform.Application.Analysis.Stores;
using SocAlytics.Platform.Application.Recordings;
using SocAlytics.Platform.Application.Registry;
using SocAlytics.Platform.Application.Segments;
using SocAlytics.Platform.Domain.Analysis;
using SocAlytics.Platform.Domain.Recordings;
using SocAlytics.Platform.Domain.Registry;

namespace SocAlytics.Platform.Application.Analysis;

/// <summary>
/// Creates the analysis run of a finalized recording set, or finds the run that already exists for the same
/// recording-set version, workflow, and workflow version. The caller owns the unit of work.
/// </summary>
public sealed class AnalysisRunCreator(
    IAnalysisRunStore runs,
    ILogicalJobStore jobs,
    IAnalysisSegmentSource segmentSource,
    RequestSegmentMaterializationHandler segmentRequests,
    WorkflowSnapshotMapper mapper,
    RunReadinessCoordinator coordinator,
    RunSupersession supersession)
{
    public async Task<(Guid RunId, bool Created)> CreateOrFindAsync(
        RecordingSetLineage lineage, ResolvedWorkflowDefinition definition, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(lineage);
        ArgumentNullException.ThrowIfNull(definition);

        await runs.LockMatchRunsAsync(lineage.MatchId, ct);
        var existingId = await runs.FindRunIdAsync(lineage.RecordingSetVersionId, definition.WorkflowId, definition.WorkflowVersion, ct);
        if (existingId is { } found)
        {
            return (found, false);
        }

        var mapped = mapper.Map(definition);
        var snapshot = new AnalysisRunSnapshotRecord(mapped.RunSnapshotJson, mapped.RunSnapshotDigest);
        var validation = mapped.Graph.Validate(definition.RequestedCapabilities);
        Guid runId;
        if (!validation.IsValid)
        {
            runId = await runs.InsertAsync(new NewAnalysisRun(
                lineage.TeamId, lineage.MatchId, lineage.RecordingSetVersionId, lineage.FinalizedAt, definition.WorkflowId, definition.WorkflowVersion,
                definition.RequestedCapabilities, definition.CompletionPolicy, AnalysisRunState.Failed, validation.Reason,
                snapshot, [], []), ct);
            await supersession.ApplyAsync(lineage.MatchId, ct);
            return (runId, true);
        }

        var segments = segmentSource.GetSegments(lineage, definition.SegmentationPolicy.DurationSeconds, definition.SegmentationPolicy.Digest);
        var planned = LogicalJobPlanner.Plan(mapped.PlannerNodes, segments);
        var nodes = definition.Nodes.Select((d, i) => new WorkflowNodeRecord(
            Guid.CreateVersion7(), d.NodeKey, d.CapabilityId, d.CapabilityVersion, d.Tier, d.ExecutionScope, i,
            WorkflowNodeState.Waiting, null, default, mapped.NodeSnapshotJson[d.NodeKey])).ToList();
        var byKey = nodes.ToDictionary(x => x.NodeKey, StringComparer.Ordinal);
        var edges = definition.Edges.Select(e => new DependencyEdgeRecord(
            byKey[e.UpstreamNodeKey].Id, byKey[e.DependentNodeKey].Id,
            AnalysisPersistedNames.TryParseDependencyKind(e.Kind, out var kind) ? kind : throw new InvalidOperationException("Undeclared dependency kind."),
            e.Predicate is null ? null : PredicateJson(e.Predicate))
        {
            CoveragePolicy = e.CoveragePolicy,
        }).ToList();

        runId = await runs.InsertAsync(new NewAnalysisRun(
            lineage.TeamId, lineage.MatchId, lineage.RecordingSetVersionId, lineage.FinalizedAt, definition.WorkflowId, definition.WorkflowVersion,
            definition.RequestedCapabilities, definition.CompletionPolicy, AnalysisRunState.Requested, null, snapshot, nodes, edges), ct);
        var videoPolicies = definition.Nodes.ToDictionary(d => d.NodeKey, d => d.VideoPolicy, StringComparer.Ordinal);
        var rejections = new Dictionary<AnalysisSegmentReference, string>();
        foreach (var p in planned)
        {
            if (p.Segment is not { } segment || videoPolicies[p.NodeKey] is not (VideoPolicies.Required or VideoPolicies.Optional))
            {
                continue;
            }

            var request = await segmentRequests.HandleAsync(new RequestSegmentMaterializationCommand(
                lineage.MatchId, lineage.TeamId, lineage.RecordingSetVersionId, segment.RecordingVersionId,
                segment.TimelineMappingDigest.ToString(), segment.SegmentationPolicyDigest.ToString(), segment.SegmentNumber,
                definition.SegmentationPolicy.Version, definition.SegmentationPolicy.DurationSeconds), ct);
            if (!request.IsAccepted)
            {
                rejections[segment] = request.RejectionReason!;
            }
        }

        await jobs.InsertAsync(planned.Select(p => new NewLogicalJob(
            Guid.CreateVersion7(), runId, byKey[p.NodeKey].Id, p.Scope,
            p.Segment?.RecordingVersionId, p.Segment?.TimelineMappingDigest.ToString(),
            p.Segment?.SegmentationPolicyDigest.ToString(), p.Segment?.SegmentNumber, LogicalJobState.Waiting,
            new LogicalJobSnapshotRecord(
                p.Snapshot.MaxAttempts, p.Snapshot.LeaseSeconds, p.Snapshot.StaleTimeoutSeconds, p.Snapshot.ExecutionTimeoutSeconds,
                p.Snapshot.AnalystProfileDigest.ToString(), p.Snapshot.ImageDigest.ToString(), p.Snapshot.ModelDigest?.ToString(),
                JsonSerializer.Serialize(p.Snapshot.Resources), p.Snapshot.SchemaVersion),
            p.Segment is { } s && videoPolicies[p.NodeKey] != VideoPolicies.Forbidden && rejections.TryGetValue(s, out var reason) ? reason : null)).ToList(), ct);
        await supersession.ApplyAsync(lineage.MatchId, ct);
        var created = await runs.LockRunAsync(runId, ct);
        if (created?.State == AnalysisRunState.Requested)
        {
            await coordinator.ApplyAsync(runId, ct);
        }

        return (runId, true);
    }

    private static string PredicateJson(ConditionalPredicate p)
    {
        var o = new Dictionary<string, string> { ["kind"] = p.Kind };
        if (p.CapabilityId is not null)
        {
            o["capabilityId"] = p.CapabilityId;
        }

        if (p.NodeKey is not null)
        {
            o["nodeKey"] = p.NodeKey;
        }

        return JsonSerializer.Serialize(o);
    }
}
