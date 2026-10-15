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

        var mode = options.Capabilities.TryGetValue(launch.CapabilityId, out var configured)
            ? configured.ResultMode
            : SimulatedResultModes.Valid;
        if (!SimulatedResultModes.All.Contains(mode))
        {
            throw new InvalidOperationException($"The simulated result mode '{mode}' is not supported.");
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

        var artifacts = new List<object>
        {
            new { name = ArtifactName, digest = ResultPublisher.Digest(artifact), sizeBytes = artifact.Length, mediaType = "application/json" },
        };
        var lineage = (job.Inputs ?? [])
            .SelectMany(i => i.AcceptedResults ?? [])
            .Where(r => r.Capability is not null)
            .Select(r => new Dictionary<string, object?>
            {
                ["acceptedResultId"] = r.AcceptedResultId,
                ["capability"] = new { id = r.Capability!.Id, version = r.Capability.Version },
                ["schemaVersion"] = r.SchemaVersion,
                ["manifestChecksum"] = r.ManifestChecksum,
            })
            .ToList();

        switch (mode)
        {
            case SimulatedResultModes.ScopeMismatch:
                scope["runId"] = Guid.NewGuid().ToString();
                break;
            case SimulatedResultModes.ProvenanceMismatch:
                provenance["imageDigest"] = "sha-256:" + new string('f', 64);
                break;
            case SimulatedResultModes.LineageMismatch when lineage.Count > 0:
                lineage[0]["manifestChecksum"] = "sha-256:" + new string('f', 64);
                break;
            case SimulatedResultModes.LineageMismatch:
                lineage.Add(new Dictionary<string, object?>
                {
                    ["acceptedResultId"] = Guid.NewGuid().ToString(),
                    ["capability"] = new { id = "unrelated-capability", version = "1.0.0" },
                    ["schemaVersion"] = "1.0.0",
                    ["manifestChecksum"] = "sha-256:" + new string('f', 64),
                });
                break;
            case SimulatedResultModes.ArtifactMissing:
                artifacts.Add(new { name = "missing-facts.json", digest = "sha-256:" + new string('e', 64), sizeBytes = 1, mediaType = "application/json" });
                break;
            case SimulatedResultModes.ValidityInvalid:
                start = end + 1;
                break;
        }

        var manifest = new Dictionary<string, object?>
        {
            ["contractVersion"] = "1.0.0",
            ["scope"] = scope,
            ["provenance"] = provenance,
            ["attempt"] = new { attemptId = launch.AttemptId, attemptNumber = launch.AttemptNumber },
            ["artifacts"] = artifacts,
            ["validityWindow"] = new { startMs = start, endMs = end },
            ["factCount"] = 0,
            ["upstreamLineage"] = lineage,
            ["outputType"] = segment is null ? "soccer-concepts" : "foundational-facts",
            ["confidence"] = 1,
            ["evidenceReferences"] = Array.Empty<string>(),
        };
        if (mode == SimulatedResultModes.ContractInvalid)
        {
            manifest.Remove("scope");
        }

        File.WriteAllText(Path.Combine(directory, ResultPublisher.ManifestName), JsonSerializer.Serialize(manifest, PlatformJson.Options));
    }
}
