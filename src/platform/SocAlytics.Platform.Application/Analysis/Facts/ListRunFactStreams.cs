using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.Analysis.Results;
using SocAlytics.Platform.Application.IdentityAccess;
using SocAlytics.Platform.Domain.Analysis;

namespace SocAlytics.Platform.Application.Analysis.Facts;

public sealed record ListRunFactStreamsQuery(Guid RunId);

public sealed record FactStreamCoverageCounts(int Accepted, int Missing, int Failed, int Blocked, int Skipped);

public sealed record FactStreamEntry(
    FactNodeRecord Node, long FactCount, FactStreamCoverageCounts Coverage);

public sealed record RunFactStreams(FactRunRecord Run, IReadOnlyList<FactStreamEntry> Streams, string ETagBasis);

public sealed class ListRunFactStreamsHandler(IUnitOfWork unitOfWork, IFactQueries queries, IAccessAuthorizer authorizer)
{
    public async Task<OperationResult<RunFactStreams>> HandleAsync(ListRunFactStreamsQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        await using var scope = await unitOfWork.BeginAsync(cancellationToken);
        var decision = await authorizer.AuthorizeTeamResourceAsync(new("analysis-run", query.RunId), TeamPermission.Read, cancellationToken);
        if (!decision.IsGranted)
        {
            return OperationFailure.NotFound();
        }

        var run = await queries.GetRunAsync(query.RunId, cancellationToken);
        if (run is null)
        {
            return OperationFailure.NotFound();
        }

        var nodes = await queries.ListNodesAsync(run.RunId, cancellationToken);
        var counts = await queries.CountFactsByNodeAsync(run.RunId, cancellationToken);
        var inputs = await queries.GetCoverageInputsAsync(run.RunId, null, cancellationToken);

        var streams = new List<FactStreamEntry>(nodes.Count);
        foreach (var node in nodes)
        {
            var windows = FactCoverageBuilder.Build(
                new FactCoverageInput(
                    node.Required,
                    [.. inputs.Jobs.Where(j => j.NodeId == node.NodeId).Select(j => new FactCoverageJob(
                        j.SegmentNumber, Enum.Parse<LogicalJobState>(j.State, ignoreCase: true), j.HasAcceptedResult))],
                    inputs.SegmentDurationMs,
                    inputs.RecordingCoverageEndMs ?? 0));
            var byState = FactCoverageBuilder.CountByState(windows);
            streams.Add(new FactStreamEntry(
                node,
                counts.GetValueOrDefault(node.NodeId),
                new FactStreamCoverageCounts(
                    byState[CoverageState.Accepted], byState[CoverageState.Missing], byState[CoverageState.Failed],
                    byState[CoverageState.Blocked], byState[CoverageState.Skipped])));
        }

        return new RunFactStreams(run, streams, $"{run.VersionBasis}|streams");
    }
}
