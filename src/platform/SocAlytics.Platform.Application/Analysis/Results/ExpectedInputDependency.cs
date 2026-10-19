using System.Text.Json;
using SocAlytics.Platform.Application.Analysis.Stores;
using SocAlytics.Platform.Domain.Analysis;

namespace SocAlytics.Platform.Application.Analysis.Results;

/// <summary>One upstream dependency of a job as its input snapshot recorded it, resolved to what a coverage report must match.</summary>
public sealed record ExpectedInputDependency(
    string CapabilityId,
    string CapabilityVersion,
    string SchemaVersion,
    CoverageMode Mode,
    IReadOnlyList<SegmentCoverage>? Coverage,
    IReadOnlySet<string> AcceptedUnits)
{
    public const string MatchUnit = "match";

    public static string SegmentUnit(string segmentId) => "segment:" + segmentId;

    public static IReadOnlyList<ExpectedInputDependency> Build(
        IReadOnlyList<InputSnapshotEntry> snapshot,
        IReadOnlyList<WorkflowNodeRecord> nodes,
        IReadOnlyList<LogicalJobRecord> runJobs,
        IReadOnlyList<AcceptedResultRecord> upstream)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var jobsById = runJobs.ToDictionary(j => j.Id);
        var resultsById = upstream.ToDictionary(r => r.Id);
        var dependencies = new List<ExpectedInputDependency>();
        foreach (var entry in snapshot)
        {
            var node = nodes.Single(n => n.NodeKey == entry.UpstreamNodeKey);
            var accepted = new HashSet<string>(StringComparer.Ordinal);
            foreach (var id in entry.AcceptedResultIds)
            {
                if (resultsById.TryGetValue(id, out var result) && jobsById.TryGetValue(result.LogicalJobId, out var job))
                {
                    accepted.Add(job.SegmentNumber is { } number ? SegmentUnit($"{job.RecordingVersionId}-{number}") : MatchUnit);
                }
            }

            dependencies.Add(new ExpectedInputDependency(
                node.CapabilityId, node.CapabilityVersion, SchemaVersionOf(node), entry.CoverageMode, entry.Coverage, accepted));
        }

        return dependencies;
    }

    private static string SchemaVersionOf(WorkflowNodeRecord node)
    {
        using var document = JsonDocument.Parse(node.SnapshotJson);
        return document.RootElement.TryGetProperty("schemaVersion", out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()!
            : string.Empty;
    }
}
