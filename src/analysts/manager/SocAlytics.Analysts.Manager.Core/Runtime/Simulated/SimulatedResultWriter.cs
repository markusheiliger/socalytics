using System.Text.Json;
using System.Text.Json.Nodes;
using SocAlytics.Analysts.Manager.Platform;
using SocAlytics.Analysts.Manager.Work;

namespace SocAlytics.Analysts.Manager.Runtime.Simulated;

/// <summary>Writes the deterministic manifest and artifact of a valid simulated result; scope, provenance, and lineage come from the job.</summary>
internal static class SimulatedResultWriter
{
    private const long SegmentWindowMs = 300_000;
    private const long MatchWindowMs = 5_400_000;
    private const string ArtifactName = "facts.json";
    private const string InputsArtifactName = "inputs.json";
    private const string FactMediaType = "application/vnd.socalytics.facts+json";
    private const long FactLengthMs = 1_000;

    public static long SegmentStartMs(int segmentNumber) => (segmentNumber - 1) * SegmentWindowMs;

    /// <summary>A job with <c>segmentMedia</c> needs the verified manifest and media the Manager staged.</summary>
    public static bool HasRequiredMedia(AttemptLaunch launch) =>
        launch.Job?.SegmentMedia is null
        || (launch.MediaDirectory is { } media
            && File.Exists(Path.Combine(media, SegmentMediaStager.ManifestName))
            && File.Exists(Path.Combine(media, SegmentMediaStager.MediaName)));

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
        var staged = launch.InputsDirectory is { } inputsDirectory && Directory.Exists(inputsDirectory)
            ? ReadStagedInputs(inputsDirectory)
            : null;
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
            var window = job.SegmentWindow;
            start = window?.StartMs ?? SegmentStartMs(segment.SegmentNumber);
            end = window?.EndMs ?? segment.SegmentNumber * SegmentWindowMs;
            scope["segment"] = new Dictionary<string, object?>
            {
                ["segmentId"] = window?.SegmentId ?? $"{segment.RecordingVersionId}-{segment.SegmentNumber}",
                ["windowStartMs"] = start,
                ["windowEndMs"] = end,
            };
        }
        else
        {
            scope["match"] = true;
        }

        var outputType = segment is null ? "soccer-concepts" : "foundational-facts";
        var factKind = segment is null ? "simulated-concept" : "simulated-observation";
        var starts = new[] { start, start + (end - start) / 2, end - FactLengthMs };
        if (mode == SimulatedResultModes.InvalidFacts)
        {
            starts[2] = end;
        }

        var artifact = JsonSerializer.SerializeToUtf8Bytes(
            new Dictionary<string, object?>
            {
                ["contractVersion"] = "1.0.0",
                ["capability"] = new { id = job.Capability.Id, version = job.Capability.Version },
                ["schemaVersion"] = job.SchemaVersion,
                ["outputType"] = outputType,
                ["facts"] = starts.Select((factStart, index) => new Dictionary<string, object?>
                {
                    ["kind"] = factKind,
                    ["startMs"] = factStart,
                    ["endMs"] = factStart + FactLengthMs,
                    ["confidence"] = 0.9,
                    ["payload"] = new { index },
                }).ToList(),
            },
            PlatformJson.Options);
        File.WriteAllBytes(Path.Combine(directory, ArtifactName), artifact);

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
            new { name = ArtifactName, digest = ResultPublisher.Digest(artifact), sizeBytes = artifact.Length, mediaType = FactMediaType },
        };
        if (staged is not null)
        {
            var inputsArtifact = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object?> { ["consumedCoverage"] = staged.Coverage }, PlatformJson.Options);
            File.WriteAllBytes(Path.Combine(directory, InputsArtifactName), inputsArtifact);
            artifacts.Add(new { name = InputsArtifactName, digest = ResultPublisher.Digest(inputsArtifact), sizeBytes = inputsArtifact.Length, mediaType = "application/json" });
        }

        var lineage = staged?.Lineage ?? (job.Inputs ?? [])
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

        var inputCoverage = staged?.InputCoverage ?? [];

        switch (mode)
        {
            case SimulatedResultModes.IgnoreInputs:
                lineage.Clear();
                break;
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
            case SimulatedResultModes.CoverageOmitted:
                inputCoverage.Clear();
                break;
            case SimulatedResultModes.CoverageMismatch when inputCoverage.Count > 0 && inputCoverage[0]["units"] is JsonArray { Count: > 0 } units:
                units[0]!["state"] = (string?)units[0]!["state"] == "accepted" ? "failed" : "accepted";
                break;
        }

        var manifest = new Dictionary<string, object?>
        {
            ["contractVersion"] = "1.1.0",
            ["scope"] = scope,
            ["provenance"] = provenance,
            ["attempt"] = new { attemptId = launch.AttemptId, attemptNumber = launch.AttemptNumber },
            ["artifacts"] = artifacts,
            ["validityWindow"] = new { startMs = start, endMs = end },
            ["factCount"] = 3,
            ["upstreamLineage"] = lineage,
            ["outputType"] = outputType,
            ["confidence"] = 1,
            ["evidenceReferences"] = Array.Empty<string>(),
        };
        if (inputCoverage.Count > 0)
        {
            manifest["inputCoverage"] = inputCoverage;
        }

        if (mode == SimulatedResultModes.ContractInvalid)
        {
            manifest.Remove("scope");
        }

        File.WriteAllText(Path.Combine(directory, ResultPublisher.ManifestName), JsonSerializer.Serialize(manifest, PlatformJson.Options));
    }

    /// <summary>Builds the lineage from the staged manifests and collects the staged coverage; the layout is contracts/staged-inputs.md.</summary>
    private static StagedInputs ReadStagedInputs(string inputsDirectory)
    {
        var lineage = new List<Dictionary<string, object?>>();
        var coverage = new SortedDictionary<string, JsonNode?>(StringComparer.Ordinal);
        var inputCoverage = new List<JsonNode>();
        foreach (var capabilityDirectory in Directory.EnumerateDirectories(inputsDirectory).Order(StringComparer.Ordinal))
        {
            var coveragePath = Path.Combine(capabilityDirectory, "coverage.json");
            if (File.Exists(coveragePath))
            {
                var document = JsonNode.Parse(File.ReadAllText(coveragePath));
                coverage[Path.GetFileName(capabilityDirectory)] = document?["coverage"];
                if (document?["upstream"] is { } upstream && upstream["capability"] is { } capability)
                {
                    var units = new JsonArray();
                    foreach (var entry in document["coverage"]?.AsArray() ?? [])
                    {
                        var unit = new JsonObject();
                        if (entry?["segmentId"] is { } segmentId)
                        {
                            unit["segmentId"] = segmentId.GetValue<string>();
                        }
                        else
                        {
                            unit["match"] = true;
                        }

                        unit["state"] = entry?["state"]?.GetValue<string>();
                        units.Add(unit);
                    }

                    inputCoverage.Add(new JsonObject
                    {
                        ["capability"] = new JsonObject
                        {
                            ["id"] = capability["id"]?.GetValue<string>(),
                            ["version"] = capability["version"]?.GetValue<string>(),
                        },
                        ["schemaVersion"] = upstream["schemaVersion"]?.GetValue<string>(),
                        ["units"] = units,
                    });
                }
            }

            foreach (var resultDirectory in Directory.EnumerateDirectories(capabilityDirectory).Order(StringComparer.Ordinal))
            {
                var bytes = File.ReadAllBytes(Path.Combine(resultDirectory, ResultPublisher.ManifestName));
                var manifest = JsonNode.Parse(bytes)!;
                var provenance = manifest["provenance"]!;
                lineage.Add(new Dictionary<string, object?>
                {
                    ["acceptedResultId"] = Path.GetFileName(resultDirectory),
                    ["capability"] = new { id = (string)provenance["capability"]!["id"]!, version = (string)provenance["capability"]!["version"]! },
                    ["schemaVersion"] = (string)provenance["schemaVersion"]!,
                    ["manifestChecksum"] = ResultPublisher.Digest(bytes),
                });
            }
        }

        return new StagedInputs(lineage, coverage, inputCoverage);
    }

    private sealed record StagedInputs(List<Dictionary<string, object?>> Lineage, SortedDictionary<string, JsonNode?> Coverage, List<JsonNode> InputCoverage);
}
