using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using SocAlytics.Platform.Application.Analysis.Results;
using SocAlytics.Platform.Domain.Recordings;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Integration.Tests.Recordings;

namespace SocAlytics.Platform.Integration.Tests.Analysis.Support;

internal sealed record FactSpec(string Kind, long StartMs, long EndMs, JsonObject? Payload = null);

internal sealed record PreparedResult(string ManifestReference, string ManifestChecksum, JsonObject Manifest);

/// <summary>Builds, declares and uploads a result manifest that matches the seeded attempt, so tests can complete attempts successfully.</summary>
internal static class ResultObjectsHelper
{
    private static readonly HttpClient Http = new();
    private static readonly byte[] Artifact = Encoding.UTF8.GetBytes("{\"facts\":[]}");

    public static string DigestOf(byte[] bytes) => Sha256Digest.FromBytes(SHA256.HashData(bytes)).ToString();

    public static async Task<PreparedResult> PrepareAsync(
        IsolatedDatabase db,
        RustFsContainerFixture rustFs,
        Guid attemptId,
        IReadOnlyCollection<Guid>? upstream = null,
        Action<JsonObject>? mutate = null,
        bool uploadManifest = true,
        IReadOnlyList<FactSpec>? facts = null,
        CancellationToken ct = default)
    {
        await using var c = new NpgsqlConnection(db.MigratorConnectionString);
        await c.OpenAsync(ct);

        var manifest = await BuildAsync(c, attemptId, upstream ?? [], ct);
        var artifact = Artifact;
        if (facts is not null)
        {
            artifact = FactArtifactBytes(manifest, facts);
            var declared = (JsonObject)manifest["artifacts"]![0]!;
            declared["digest"] = DigestOf(artifact);
            declared["sizeBytes"] = artifact.Length;
            declared["mediaType"] = "application/vnd.socalytics.facts+json";
            manifest["factCount"] = facts.Count;
        }

        mutate?.Invoke(manifest);
        var manifestBytes = Encoding.UTF8.GetBytes(manifest.ToJsonString());
        var runId = Guid.Parse(manifest["scope"]!["runId"]!.GetValue<string>());
        var jobId = Guid.Parse(manifest["scope"]!["logicalJobId"]!.GetValue<string>());
        var location = IssueResultGrantsHandler.ResultLocation(runId, jobId, attemptId);

        await DeclareAsync(c, attemptId, "manifest.json", manifestBytes, ct);
        await DeclareAsync(c, attemptId, "facts.json", artifact, ct);
        if (uploadManifest)
        {
            await PutAsync(rustFs, location + "manifest.json", manifestBytes, ct);
        }

        await PutAsync(rustFs, location + "facts.json", artifact, ct);
        return new PreparedResult(location + "manifest.json", DigestOf(manifestBytes), manifest);
    }

    private static byte[] FactArtifactBytes(JsonObject manifest, IReadOnlyList<FactSpec> facts)
    {
        var provenance = manifest["provenance"]!;
        var artifact = new JsonObject
        {
            ["contractVersion"] = "1.0.0",
            ["capability"] = new JsonObject
            {
                ["id"] = provenance["capability"]!["id"]!.GetValue<string>(),
                ["version"] = provenance["capability"]!["version"]!.GetValue<string>(),
            },
            ["schemaVersion"] = provenance["schemaVersion"]!.GetValue<string>(),
            ["outputType"] = manifest["outputType"]!.GetValue<string>(),
            ["facts"] = new JsonArray(facts.Select(f => (JsonNode)new JsonObject
            {
                ["kind"] = f.Kind,
                ["startMs"] = f.StartMs,
                ["endMs"] = f.EndMs,
                ["confidence"] = 0.5,
                ["payload"] = f.Payload?.DeepClone() ?? new JsonObject { ["k"] = f.Kind },
            }).ToArray()),
        };
        return Encoding.UTF8.GetBytes(artifact.ToJsonString());
    }

    public static async Task ApplyAsync(
        IsolatedDatabase db, RustFsContainerFixture rustFs, Guid attemptId, JsonObject body, IReadOnlyCollection<Guid>? upstream = null, CancellationToken ct = default)
    {
        var prepared = await PrepareAsync(db, rustFs, attemptId, upstream, ct: ct);
        body["result"] = new JsonObject { ["manifestReference"] = prepared.ManifestReference, ["manifestChecksum"] = prepared.ManifestChecksum };
    }

    private static async Task DeclareAsync(NpgsqlConnection c, Guid attemptId, string name, byte[] bytes, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO socalytics.analysis_result_object_declarations (attempt_id, name, size_bytes, digest, declared_at)
            VALUES (@a, @n, @s, @d, now())
            ON CONFLICT (attempt_id, name) DO NOTHING
            """, c);
        cmd.Parameters.AddWithValue("a", attemptId);
        cmd.Parameters.AddWithValue("n", name);
        cmd.Parameters.AddWithValue("s", (long)bytes.Length);
        cmd.Parameters.AddWithValue("d", DigestOf(bytes));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task PutAsync(RustFsContainerFixture rustFs, string key, byte[] body, CancellationToken ct)
    {
        var grant = rustFs.CreateObjectStorage()
            .PresignPutObject(key, body.Length, Sha256Digest.FromBytes(SHA256.HashData(body)), DateTimeOffset.UtcNow.AddMinutes(5));
        using var request = new HttpRequestMessage(HttpMethod.Put, grant.Url) { Content = new ByteArrayContent(body) };
        foreach (var (name, value) in grant.RequiredHeaders)
        {
            if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                request.Content.Headers.ContentLength = long.Parse(value, CultureInfo.InvariantCulture);
            }
            else
            {
                request.Headers.TryAddWithoutValidation(name, value);
            }
        }

        (await Http.SendAsync(request, ct)).EnsureSuccessStatusCode();
    }

    private static string Pad(string version)
    {
        var parts = version.Split('.');
        return string.Join('.', parts.Concat(Enumerable.Repeat("0", Math.Max(0, 3 - parts.Length))).Take(3));
    }

    private static async Task<JsonObject> BuildAsync(NpgsqlConnection c, Guid attemptId, IReadOnlyCollection<Guid> upstream, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            """
            SELECT a.attempt_number, j.id, j.run_id, j.node_id, j.execution_scope, j.segment_number,
                   r.team_id, r.match_id, r.recording_set_version_id, r.workflow_id, r.workflow_version, rs.snapshot::text,
                   n.capability_id, n.capability_version, ns.snapshot::text,
                   js.analyst_profile_digest, js.image_digest, js.model_digest, js.schema_version
            FROM socalytics.analysis_execution_attempts a
            JOIN socalytics.analysis_logical_jobs j ON j.id = a.logical_job_id
            JOIN socalytics.analysis_runs r ON r.id = j.run_id
            JOIN socalytics.analysis_run_snapshots rs ON rs.run_id = r.id
            JOIN socalytics.analysis_workflow_nodes n ON n.id = j.node_id
            JOIN socalytics.analysis_node_snapshots ns ON ns.node_id = n.id
            JOIN socalytics.analysis_logical_job_snapshots js ON js.logical_job_id = j.id
            WHERE a.id = @a
            """, c);
        cmd.Parameters.AddWithValue("a", attemptId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct))
        {
            throw new InvalidOperationException("Attempt not found.");
        }

        var attemptNumber = r.GetInt32(0);
        var jobId = r.GetGuid(1);
        var runId = r.GetGuid(2);
        var nodeId = r.GetGuid(3);
        var matchScope = r.GetString(4) == "match";
        var segmentNumber = r.IsDBNull(5) ? (int?)null : r.GetInt32(5);
        var runSnapshot = JsonNode.Parse(r.GetString(11))!;
        var nodeSnapshot = JsonNode.Parse(r.GetString(14))!;
        var profileDigest = r.GetString(15);
        var imageDigest = r.GetString(16);
        var modelDigest = r.IsDBNull(17) ? null : r.GetString(17);

        var scope = new JsonObject
        {
            ["teamId"] = r.GetGuid(6).ToString(),
            ["matchId"] = r.GetGuid(7).ToString(),
            ["recordingSetVersionId"] = r.GetGuid(8).ToString(),
            ["runId"] = runId.ToString(),
            ["nodeId"] = nodeId.ToString(),
            ["logicalJobId"] = jobId.ToString(),
        };
        JsonObject validity;
        if (matchScope)
        {
            scope["match"] = true;
            validity = new JsonObject { ["startMs"] = 0, ["endMs"] = 5400000 };
        }
        else
        {
            var duration = runSnapshot["segmentationPolicy"]!["durationSeconds"]!.GetValue<int>() * 1000L;
            var start = (segmentNumber!.Value - 1) * duration;
            scope["segment"] = new JsonObject { ["segmentId"] = "segment-" + segmentNumber, ["windowStartMs"] = start, ["windowEndMs"] = start + duration };
            validity = new JsonObject { ["startMs"] = start, ["endMs"] = start + duration };
        }

        var provenance = new JsonObject
        {
            ["capability"] = new JsonObject { ["id"] = r.GetString(12), ["version"] = Pad(r.GetString(13)) },
            ["schemaVersion"] = Pad(r.GetString(18)),
            ["analystProfile"] = new JsonObject { ["id"] = nodeSnapshot["analystProfileId"]!.GetValue<string>(), ["digest"] = profileDigest },
            ["imageDigest"] = imageDigest,
            ["policyVersions"] = new JsonArray((nodeSnapshot["policyVersions"] as JsonObject ?? [])
                .Select(p => (JsonNode)new JsonObject { ["id"] = p.Key, ["version"] = Pad(p.Value!.GetValue<string>()) }).ToArray()),
            ["workflow"] = new JsonObject { ["id"] = r.GetString(9), ["version"] = Pad(r.GetString(10)) },
        };
        if (modelDigest is not null)
        {
            provenance["modelDigest"] = modelDigest;
        }

        await r.CloseAsync();

        var lineage = new JsonArray();
        foreach (var id in upstream)
        {
            await using var up = new NpgsqlCommand(
                "SELECT capability_id, capability_version, schema_version, manifest_checksum FROM socalytics.analysis_accepted_results WHERE id = @id", c);
            up.Parameters.AddWithValue("id", id);
            await using var ur = await up.ExecuteReaderAsync(ct);
            if (await ur.ReadAsync(ct))
            {
                lineage.Add(new JsonObject
                {
                    ["acceptedResultId"] = id.ToString(),
                    ["capability"] = new JsonObject { ["id"] = ur.GetString(0), ["version"] = Pad(ur.GetString(1)) },
                    ["schemaVersion"] = Pad(ur.GetString(2)),
                    ["manifestChecksum"] = ur.GetString(3),
                });
            }
        }

        return new JsonObject
        {
            ["contractVersion"] = "1.0.0",
            ["scope"] = scope,
            ["provenance"] = provenance,
            ["attempt"] = new JsonObject { ["attemptId"] = attemptId.ToString(), ["attemptNumber"] = attemptNumber },
            ["artifacts"] = new JsonArray(new JsonObject
            {
                ["name"] = "facts.json",
                ["digest"] = DigestOf(Artifact),
                ["sizeBytes"] = Artifact.Length,
                ["mediaType"] = "application/json",
            }),
            ["validityWindow"] = validity,
            ["factCount"] = 0,
            ["upstreamLineage"] = lineage,
            ["outputType"] = "foundational-facts",
            ["confidence"] = 1,
            ["evidenceReferences"] = new JsonArray(),
        };
    }
}
