using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Analysis.Results;
using SocAlytics.Platform.Domain.Analysis;

namespace SocAlytics.Platform.Application.Analysis.Facts;

/// <summary>Reads the fact streams of an already-authorized run inside the caller's unit of work.</summary>
public interface IRunFactStreamReader
{
    Task<OperationResult<RunFactStreams>> ReadAsync(AuthorizedRunScope scope, CancellationToken cancellationToken);
}

internal sealed class RunFactStreamReader(IFactQueries queries) : IRunFactStreamReader
{
    public async Task<OperationResult<RunFactStreams>> ReadAsync(AuthorizedRunScope scope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);

        var run = await queries.GetRunAsync(scope.RunId, cancellationToken);
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
