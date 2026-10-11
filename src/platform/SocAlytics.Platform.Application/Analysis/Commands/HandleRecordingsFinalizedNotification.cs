using System.Text.Json;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.Analysis.Stores;
using SocAlytics.Platform.Application.Recordings;
using SocAlytics.Platform.Application.Registry;
using SocAlytics.Platform.Domain.Analysis;
using SocAlytics.Platform.Domain.Recordings;

namespace SocAlytics.Platform.Application.Analysis.Commands;

public sealed record FinalizedNotificationMember(
    int Position, Guid RecordingVersionId, string RecordingContentDigest, Guid TimelineMappingId, string TimelineMappingDigest);

public sealed record RecordingsFinalizedNotification(
    Guid EventId,
    string ContractVersion,
    DateTimeOffset OccurredAt,
    Guid RecordingSetVersionId,
    Guid MatchId,
    Guid TeamId,
    IReadOnlyList<FinalizedNotificationMember> Members);

public abstract record NotificationOutcome
{
    private NotificationOutcome()
    {
    }

    public sealed record RunCreated(Guid RunId) : NotificationOutcome;

    public sealed record RunExists(Guid RunId) : NotificationOutcome;

    public sealed record Rejected(string Category) : NotificationOutcome;

    public sealed record DependencyUnavailable : NotificationOutcome;

    public sealed record WorkflowUnresolvable(string Category) : NotificationOutcome;
}

public sealed class HandleRecordingsFinalizedNotificationHandler(
    IUnitOfWork unitOfWork,
    INotificationReceiptStore receipts,
    IAnalysisRunStore runs,
    ILogicalJobStore jobs,
    IWorkflowDefinitionResolver resolver,
    IRecordingSetLookup lookup,
    IAnalysisSegmentSource segmentSource,
    WorkflowSnapshotMapper mapper,
    RunReadinessCoordinator coordinator,
    TimeProvider time)
{
    private const string MessageType = "recordings-finalized";

    public async Task<NotificationOutcome> HandleAsync(RecordingsFinalizedNotification notification, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(notification);

        try
        {
            return await ExecuteAsync(notification, cancellationToken);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // A concurrent duplicate may have won the receipt or run unique constraint; return its recorded outcome.
            await using var scope = await unitOfWork.BeginAsync(cancellationToken);
            var existing = await ExistingAsync(notification.EventId, cancellationToken);
            if (existing is null)
            {
                throw;
            }

            return existing;
        }
    }

    private async Task<NotificationOutcome?> ExistingAsync(Guid eventId, CancellationToken ct)
    {
        var receipt = await receipts.GetAsync(eventId, ct);
        return receipt is null ? null : ToOutcome(receipt);
    }

    private static NotificationOutcome ToOutcome(NotificationReceiptRecord r) => r.Outcome switch
    {
        "run-created" => new NotificationOutcome.RunCreated(r.RunId!.Value),
        "run-exists" => new NotificationOutcome.RunExists(r.RunId!.Value),
        _ => new NotificationOutcome.Rejected(r.RejectionCategory ?? "schema-invalid"),
    };

    private async Task<NotificationOutcome> ExecuteAsync(RecordingsFinalizedNotification n, CancellationToken ct)
    {
        await using var scope = await unitOfWork.BeginAsync(ct);
        var now = time.GetUtcNow();

        async Task<NotificationOutcome> Record(string outcome, string? category, Guid? runId)
        {
            var result = await receipts.TryInsertAsync(
                new NotificationReceiptRecord(n.EventId, MessageType, n.RecordingSetVersionId, outcome, category, runId, now), ct);
            if (!result.Inserted)
            {
                await scope.RollbackAsync(ct);
                return ToOutcome(result.Receipt);
            }

            await scope.CommitAsync(ct);
            return ToOutcome(result.Receipt);
        }

        var recorded = await receipts.GetAsync(n.EventId, ct);
        if (recorded is not null)
        {
            await scope.RollbackAsync(ct);
            return ToOutcome(recorded);
        }

        var resolution = await resolver.ResolveDefaultAsync(ct);
        if (resolution is not WorkflowResolution.ResolvedResult resolved)
        {
            await scope.RollbackAsync(ct);
            return new NotificationOutcome.WorkflowUnresolvable(((WorkflowResolution.UnavailableResult)resolution).Category);
        }

        RecordingSetLineage? lineage;
        try
        {
            lineage = await lookup.GetAsync(n.RecordingSetVersionId, ct);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            await scope.RollbackAsync(ct);
            return new NotificationOutcome.DependencyUnavailable();
        }

        if (lineage is null)
        {
            return await Record("rejected", "lineage-not-found", null);
        }

        if (lineage.MatchId != n.MatchId || lineage.TeamId != n.TeamId)
        {
            return await Record("rejected", "scope-mismatch", null);
        }

        if (!MembersMatch(n.Members, lineage.Members))
        {
            return await Record("rejected", "lineage-mismatch", null);
        }

        var definition = resolved.Definition;
        var existingId = await runs.FindRunIdAsync(lineage.RecordingSetVersionId, definition.WorkflowId, definition.WorkflowVersion, ct);
        if (existingId is { } found)
        {
            return await Record("run-exists", null, found);
        }

        var mapped = mapper.Map(definition);
        var snapshot = new AnalysisRunSnapshotRecord(mapped.RunSnapshotJson, mapped.RunSnapshotDigest);
        var validation = mapped.Graph.Validate(definition.RequestedCapabilities);
        Guid runId;
        if (!validation.IsValid)
        {
            runId = await runs.InsertAsync(new NewAnalysisRun(
                lineage.TeamId, lineage.MatchId, lineage.RecordingSetVersionId, definition.WorkflowId, definition.WorkflowVersion,
                definition.RequestedCapabilities, definition.CompletionPolicy, AnalysisRunState.Failed, validation.Reason,
                snapshot, [], []), ct);
            return await Record("run-created", null, runId);
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
            e.Predicate is null ? null : PredicateJson(e.Predicate))).ToList();

        runId = await runs.InsertAsync(new NewAnalysisRun(
            lineage.TeamId, lineage.MatchId, lineage.RecordingSetVersionId, definition.WorkflowId, definition.WorkflowVersion,
            definition.RequestedCapabilities, definition.CompletionPolicy, AnalysisRunState.Requested, null, snapshot, nodes, edges), ct);
        await jobs.InsertAsync(planned.Select(p => new NewLogicalJob(
            Guid.CreateVersion7(), runId, byKey[p.NodeKey].Id, p.Scope,
            p.Segment?.RecordingVersionId, p.Segment?.TimelineMappingDigest.ToString(),
            p.Segment?.SegmentationPolicyDigest.ToString(), p.Segment?.SegmentNumber, LogicalJobState.Waiting,
            new LogicalJobSnapshotRecord(
                p.Snapshot.MaxAttempts, p.Snapshot.LeaseSeconds, p.Snapshot.StaleTimeoutSeconds, p.Snapshot.ExecutionTimeoutSeconds,
                p.Snapshot.AnalystProfileDigest.ToString(), p.Snapshot.ImageDigest.ToString(), p.Snapshot.ModelDigest?.ToString(),
                JsonSerializer.Serialize(p.Snapshot.Resources), p.Snapshot.SchemaVersion))).ToList(), ct);
        await runs.LockRunAsync(runId, ct);
        await coordinator.ApplyAsync(runId, ct);
        return await Record("run-created", null, runId);
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

    private static bool MembersMatch(IReadOnlyList<FinalizedNotificationMember> given, IReadOnlyList<RecordingSetLineageMember> actual)
    {
        if (given.Count != actual.Count)
        {
            return false;
        }

        var left = given.OrderBy(m => m.Position).ToList();
        var right = actual.OrderBy(m => m.Position).ToList();
        for (var i = 0; i < left.Count; i++)
        {
            if (left[i].RecordingVersionId != right[i].RecordingVersionId
                || left[i].TimelineMappingId != right[i].TimelineMappingId
                || !string.Equals(left[i].RecordingContentDigest, right[i].RecordingContentDigest, StringComparison.Ordinal)
                || !string.Equals(left[i].TimelineMappingDigest, right[i].TimelineMappingDigest, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}
