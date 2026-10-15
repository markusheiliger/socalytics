using System.Text.Json;
using SocAlytics.Analysts.Manager.Platform;
using SocAlytics.Analysts.Manager.Work;

namespace SocAlytics.Analysts.Manager.Runtime.Simulated;

/// <summary>Writes the deterministic manifest and artifact of a valid simulated result; scope, provenance, and lineage come from the job.</summary>
internal static class SimulatedResultWriter
{
    private const long SegmentWindowMs = 300_000;
    private const long MatchWindowMs = 5_400_000;
    private const string ArtifactName = "facts.json";

    public static void Write(AttemptLaunch launch, SimulatedAnalystOptions options)
    {
        if (launch.Job is not { } job || launch.ResultDirectory is not { } directory)
        {
            return;
        }

        Directory.CreateDirectory(directory);
        var artifact = System.Text.Encoding.UTF8.GetBytes($$"""{"capability":"{{job.Capability.Id}}","facts":[]}""");
        File.WriteAllBytes(Path.Combine(directory, ArtifactName), artifact);

        var segment = job.Segment;
        var scope = new Dictionary<string, object?>
        {
            ["teamId"] = job.TeamId,
            ["matchId"] = job.MatchId,
            ["recordingSetVersionId"] = job.RecordingSetVersionId ?? string.Empty,
            ["runId"] = job.RunId,
            ["nodeId"] = job.NodeId,
            ["logicalJobId"] = job.LogicalJobId,
        };
        long start = 0;
        long end = MatchWindowMs;
        if (segment is not null)
        {
            start = (segment.SegmentNumber - 1) * SegmentWindowMs;
            end = segment.SegmentNumber * SegmentWindowMs;
            scope["segment"] = new Dictionary<string, object?>
            {
                ["segmentId"] = $"{segment.RecordingVersionId}-{segment.SegmentNumber}",
                ["windowStartMs"] = start,
                ["windowEndMs"] = end,
            };
        }
        else
        {
            scope["match"] = true;
        }

        var provenance = new Dictionary<string, object?>
        {
            ["capability"] = new { id = job.Capability.Id, version = job.Capability.Version },
            ["schemaVersion"] = job.SchemaVersion,
            ["analystProfile"] = new { id = job.Pinned.AnalystProfileId ?? string.Empty, digest = job.Pinned.AnalystProfileDigest },
            ["imageDigest"] = job.Pinned.ImageDigest,
            ["policyVersions"] = (job.Pinned.Policies ?? []).Select(p => new { id = p.PolicyId, version = p.Version }).ToList(),
            ["workflow"] = new { id = options.WorkflowId, version = options.WorkflowVersion },
        };
        if (job.Pinned.ModelDigest is { } model)
        {
            provenance["modelDigest"] = model;
        }

        var manifest = new Dictionary<string, object?>
        {
            ["contractVersion"] = "1.0.0",
            ["scope"] = scope,
            ["provenance"] = provenance,
            ["attempt"] = new { attemptId = launch.AttemptId, attemptNumber = launch.AttemptNumber },
            ["artifacts"] = new[]
            {
                new { name = ArtifactName, digest = ResultPublisher.Digest(artifact), sizeBytes = artifact.Length, mediaType = "application/json" },
            },
            ["validityWindow"] = new { startMs = start, endMs = end },
            ["factCount"] = 0,
            ["upstreamLineage"] = (job.Inputs ?? [])
                .SelectMany(i => i.AcceptedResults ?? [])
                .Where(r => r.Capability is not null)
                .Select(r => new
                {
                    acceptedResultId = r.AcceptedResultId,
                    capability = new { id = r.Capability!.Id, version = r.Capability.Version },
                    schemaVersion = r.SchemaVersion,
                    manifestChecksum = r.ManifestChecksum,
                })
                .ToList(),
            ["outputType"] = segment is null ? "soccer-concepts" : "foundational-facts",
            ["confidence"] = 1,
            ["evidenceReferences"] = Array.Empty<string>(),
        };
        File.WriteAllText(Path.Combine(directory, ResultPublisher.ManifestName), JsonSerializer.Serialize(manifest, PlatformJson.Options));
    }
}
