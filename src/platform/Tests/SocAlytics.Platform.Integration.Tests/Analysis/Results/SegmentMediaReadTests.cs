using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Domain.Recordings;
using SocAlytics.Platform.Integration.Tests.Analysis.Support;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Integration.Tests.Recordings;
using SocAlytics.Platform.Migrator;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Analysis.Results;

public sealed class SegmentMediaReadTests(PostgresContainerFixture postgres, NatsContainerFixture nats, RustFsContainerFixture rustFs)
{
    private static readonly HttpClient Http = new();
    private static readonly byte[] Media = Encoding.UTF8.GetBytes("synthetic segment media");

    private sealed record Seeded(Guid JobId, Guid Occurrence, string MediaDigest, string ManifestChecksum, byte[] ManifestBytes);

    private sealed record Claimed(Guid Attempt, long Token, DateTimeOffset LeaseExpiresAt);

    private static string DigestOf(byte[] bytes) => Sha256Digest.FromBytes(SHA256.HashData(bytes)).ToString();

    private static async Task ExecAsync(NpgsqlConnection c, string sql, CancellationToken ct, params (string Name, object Value)[] parameters)
    {
        await using var cmd = new NpgsqlCommand(sql, c);
        foreach (var (name, value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value);
        }

        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<string?> TextAsync(IsolatedDatabase db, string sql, CancellationToken ct)
    {
        await using var c = new NpgsqlConnection(db.MigratorConnectionString);
        await c.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, c);
        return (await cmd.ExecuteScalarAsync(ct))?.ToString();
    }

    private async Task<(IsolatedDatabase Db, AnalysisTestHost Host)> StartAsync(CancellationToken ct)
    {
        var db = await postgres.CreateDatabaseAsync(ct);
        (await MigratorHarness.RunAsync(db, TestMigrationCatalogs.Platform(), ct)).ExitCode.ShouldBe(MigratorExitCode.Success);
        var host = new AnalysisTestHost(db, nats, rustFs, managerAuthentication: ManagerAuthentication.Test);
        host.Time.SetUtcNow(DateTimeOffset.UtcNow);
        await host.StartAsync(ct);
        return (db, host);
    }

    private async Task PutAsync(string key, byte[] body, CancellationToken ct)
    {
        var grant = rustFs.CreateObjectStorage()
            .PresignPutObject(key, body.Length, Sha256Digest.FromBytes(SHA256.HashData(body)), DateTimeOffset.UtcNow.AddMinutes(5));
        using var request = new HttpRequestMessage(HttpMethod.Put, grant.Url) { Content = new ByteArrayContent(body) };
        foreach (var (name, value) in grant.RequiredHeaders)
        {
            if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                request.Content.Headers.ContentLength = long.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
            }
            else
            {
                request.Headers.TryAddWithoutValidation(name, value);
            }
        }

        using var response = await Http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
    }

    private static byte[] ManifestOf(string mediaDigest)
    {
        var manifest = new JsonObject
        {
            ["contractVersion"] = "1.0.0",
            ["identity"] = new JsonObject
            {
                ["recordingVersionId"] = "recording-version-0001",
                ["timelineMappingDigest"] = "sha-256:" + new string('a', 64),
                ["segmentationPolicyDigest"] = "sha-256:" + new string('b', 64),
                ["segmentNumber"] = 1,
            },
            ["segmentationPolicy"] = new JsonObject { ["version"] = 1, ["durationSeconds"] = 10 },
            ["nominalWindow"] = new JsonObject { ["startMs"] = 0, ["endMs"] = 10000 },
            ["requestedSourceInterval"] = new JsonObject { ["startMs"] = 3600000, ["endMs"] = 3610000 },
            ["encodedInterval"] = new JsonObject { ["startMs"] = 3599500, ["endMs"] = 3610000 },
            ["timelineSpans"] = new JsonArray(new JsonObject { ["mediaStartMs"] = 3600000, ["mediaEndMs"] = 3610000, ["matchStartMs"] = 0 }),
            ["media"] = new JsonObject
            {
                ["artifactReference"] = "segment.mp4",
                ["mediaType"] = "video/mp4",
                ["digest"] = mediaDigest,
                ["sizeBytes"] = Media.Length,
            },
            ["producer"] = new JsonObject { ["toolchain"] = "ffmpeg 7.1" },
            ["createdAt"] = "2026-10-17T22:30:00Z",
        };
        return Encoding.UTF8.GetBytes(manifest.ToJsonString());
    }

    // A ready segment job of a running run. With pinned media the objects are stored; a tampered pin changes the attested digest.
    private async Task<Seeded> SeedAsync(IsolatedDatabase db, CancellationToken ct, bool pinned = true, bool tamperedPin = false)
    {
        Guid run = Guid.NewGuid(), node = Guid.NewGuid(), job = Guid.NewGuid(), occ = Guid.NewGuid();
        var at = DateTimeOffset.UtcNow;
        var mediaDigest = DigestOf(Media);
        var manifestBytes = ManifestOf(mediaDigest);
        var checksum = DigestOf(manifestBytes);
        var prefix = $"segments/{Guid.NewGuid()}/";
        await PutAsync(prefix + "segment-manifest.json", manifestBytes, ct);
        await PutAsync(prefix + "segment.mp4", Media, ct);

        var pin = JsonSerializer.Serialize(new
        {
            VideoPolicy = "required",
            ManifestChecksum = checksum,
            ManifestReference = prefix + "segment-manifest.json",
            MediaDigest = tamperedPin ? "sha-256:" + new string('f', 64) : mediaDigest,
            MediaSizeBytes = (long)Media.Length,
        });

        await using var c = new NpgsqlConnection(db.MigratorConnectionString);
        await c.OpenAsync(ct);
        await ExecAsync(c,
            """
            INSERT INTO socalytics.analysis_runs (id, team_id, match_id, recording_set_version_id, recording_set_finalized_at, workflow_id, workflow_version, requested_capabilities, completion_policy, state, created_at, running_at, updated_at)
            VALUES (@run, @g, @g, @g, @at, 'wf', '1.0.0', ARRAY['cap-seg'], 'all-requested-capabilities', 'Running', @at, @at, @at);
            INSERT INTO socalytics.analysis_workflow_nodes (id, run_id, node_key, capability_id, capability_version, tier, execution_scope, ordinal, state, updated_at)
            VALUES (@node, @run, 'seg', 'cap-seg', '1.0.0', 'low-level', 'segment', 0, 'Active', @at);
            INSERT INTO socalytics.analysis_run_snapshots (run_id, snapshot, snapshot_digest)
            VALUES (@run, '{"segmentationPolicy":{"durationSeconds":10}}'::jsonb, 'sha-256:a');
            INSERT INTO socalytics.analysis_node_snapshots (node_id, run_id, snapshot)
            VALUES (@node, @run, '{"analystProfileId":"profile","policyVersions":{},"schemaVersion":"1.0.0","videoPolicy":"required"}'::jsonb);
            INSERT INTO socalytics.analysis_logical_jobs (id, run_id, node_id, execution_scope, recording_version_id, timeline_mapping_digest, segmentation_policy_digest, segment_number, state, updated_at)
            VALUES (@job, @run, @node, 'segment', @rec, 'sha-256:a', 'sha-256:b', 1, 'Waiting', @at);
            INSERT INTO socalytics.analysis_logical_job_snapshots (logical_job_id, max_attempts, lease_seconds, stale_timeout_seconds, execution_timeout_seconds, analyst_profile_digest, image_digest, model_digest, resources, schema_version)
            VALUES (@job, 2, 30, 90, 600, 'sha-256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', 'sha-256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', 'sha-256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', '{}'::jsonb, '1.0.0');
            INSERT INTO socalytics.analysis_logical_job_inputs (logical_job_id, inputs, recorded_at, segment_media) VALUES (@job, '[]'::jsonb, @at, @pin::jsonb);
            INSERT INTO socalytics.analysis_readiness_occurrences (id, logical_job_id, sequence, cause, created_at) VALUES (@occ, @job, 1, 'initial', @at);
            UPDATE socalytics.analysis_logical_jobs SET state = 'Ready', current_readiness_occurrence_id = @occ WHERE id = @job;
            """, ct,
            ("run", run), ("node", node), ("job", job), ("occ", occ), ("g", Guid.NewGuid()), ("rec", Guid.NewGuid()), ("at", at),
            ("pin", pinned ? pin : (object)DBNull.Value));
        return new Seeded(job, occ, mediaDigest, checksum, manifestBytes);
    }

    private static async Task<Claimed> ClaimAsync(Seeded seed, TestManagerClient manager, CancellationToken ct)
    {
        using var response = await manager.ClaimAsync(seed.JobId, seed.Occurrence, "key-" + seed.JobId, ct: ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var a = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct)).RootElement;
        return new Claimed(a.GetProperty("attemptId").GetGuid(), a.GetProperty("fencingToken").GetInt64(), a.GetProperty("leaseExpiresAt").GetDateTimeOffset());
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body)> BodyAsync(HttpResponseMessage response, CancellationToken ct) =>
        (response.StatusCode, JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct)).RootElement);

    [Fact]
    public async Task ServesTheManifestAndTwoLeaseBoundedReadGrantsWithoutSourceLocations()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, host) = await StartAsync(ct);
        await using var _ = host;
        var seed = await SeedAsync(db, ct);
        using var m = new TestManagerClient(host.Factory);
        var c = await ClaimAsync(seed, m, ct);

        using var response = await m.GetSegmentMediaAsync(c.Attempt, $"fencingToken={c.Token}", ct);
        response.Headers.CacheControl!.NoStore.ShouldBeTrue();
        var (status, body) = await BodyAsync(response, ct);
        status.ShouldBe(HttpStatusCode.OK, body.ToString());

        var validation = host.Services.GetRequiredService<IContractValidator>().Validate(
            SocAlytics.Platform.Application.Abstractions.ContractSchemaIds.SegmentMediaV1, body);
        validation.IsValid.ShouldBeTrue(string.Join("; ", validation.Errors.Select(e => e.Field + ":" + e.Keyword)));
        body.GetProperty("attemptId").GetGuid().ShouldBe(c.Attempt);
        body.GetProperty("logicalJobId").GetGuid().ShouldBe(seed.JobId);
        body.GetProperty("manifestChecksum").GetString().ShouldBe(seed.ManifestChecksum);
        body.GetProperty("manifest").GetProperty("media").GetProperty("digest").GetString().ShouldBe(seed.MediaDigest);

        var grants = body.GetProperty("grants").EnumerateArray().ToList();
        grants.Select(g => g.GetProperty("name").GetString()).ShouldBe(["segment-manifest.json", "segment.mp4"]);
        foreach (var g in grants)
        {
            g.GetProperty("expiresAt").GetDateTimeOffset().ShouldBeLessThanOrEqualTo(c.LeaseExpiresAt.AddSeconds(1));
        }

        using var manifest = await Http.GetAsync(grants[0].GetProperty("url").GetString(), ct);
        DigestOf(await manifest.Content.ReadAsByteArrayAsync(ct)).ShouldBe(seed.ManifestChecksum);
        using var media = await Http.GetAsync(grants[1].GetProperty("url").GetString(), ct);
        DigestOf(await media.Content.ReadAsByteArrayAsync(ct)).ShouldBe(seed.MediaDigest);

        var text = body.ToString();
        text.ShouldNotContain("sourceObjectKey");
        text.ShouldNotContain("recordings/");
    }

    [Fact]
    public async Task RefusesStaleFencedMissingTokenAndNotCurrentAttempts()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, host) = await StartAsync(ct);
        await using var _ = host;
        var seed = await SeedAsync(db, ct);
        using var m = new TestManagerClient(host.Factory);
        var c = await ClaimAsync(seed, m, ct);

        using var unknown = await m.GetSegmentMediaAsync(Guid.NewGuid(), $"fencingToken={c.Token}", ct);
        unknown.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        using var missing = await m.GetSegmentMediaAsync(c.Attempt, "", ct);
        missing.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        using var wrong = await m.GetSegmentMediaAsync(c.Attempt, $"fencingToken={c.Token + 1}", ct);
        var (wrongStatus, wrongBody) = await BodyAsync(wrong, ct);
        wrongStatus.ShouldBe(HttpStatusCode.Conflict);
        wrongBody.GetProperty("code").GetString().ShouldBe("attempt-obsolete");

        await using var conn = new NpgsqlConnection(db.MigratorConnectionString);
        await conn.OpenAsync(ct);
        await ExecAsync(conn, "UPDATE socalytics.analysis_logical_jobs SET current_attempt_id = NULL WHERE id = @j", ct, ("j", seed.JobId));
        using var notCurrent = await m.GetSegmentMediaAsync(c.Attempt, $"fencingToken={c.Token}", ct);
        notCurrent.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        await ExecAsync(conn, "UPDATE socalytics.analysis_logical_jobs SET current_attempt_id = @a WHERE id = @j", ct, ("j", seed.JobId), ("a", c.Attempt));
        await ExecAsync(conn, "UPDATE socalytics.analysis_execution_attempts SET state = 'Accepted', closed_at = now() WHERE id = @a", ct, ("a", c.Attempt));
        using var completed = await m.GetSegmentMediaAsync(c.Attempt, $"fencingToken={c.Token}", ct);
        completed.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task RefusesExpiredLeases()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, host) = await StartAsync(ct);
        await using var _ = host;
        var seed = await SeedAsync(db, ct);
        using var m = new TestManagerClient(host.Factory);
        var c = await ClaimAsync(seed, m, ct);

        host.Time.SetUtcNow(c.LeaseExpiresAt.AddSeconds(1));
        using var expired = await m.GetSegmentMediaAsync(c.Attempt, $"fencingToken={c.Token}", ct);
        expired.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task RefusesForeignManagersAndAuditsThemWithoutUrls()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, host) = await StartAsync(ct);
        await using var _ = host;
        var seed = await SeedAsync(db, ct);
        using var m = new TestManagerClient(host.Factory);
        using var other = new TestManagerClient(host.Factory);
        var c = await ClaimAsync(seed, m, ct);

        using var foreign = await other.GetSegmentMediaAsync(c.Attempt, $"fencingToken={c.Token}", ct);
        foreign.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        (await TextAsync(db, "SELECT string_agg(reason_code, ',') FROM socalytics.security_audit_event WHERE event_type = 'analysis.segment-media-read-refused'", ct))
            .ShouldBe("foreign-attempt");

        using var ok = await m.GetSegmentMediaAsync(c.Attempt, $"fencingToken={c.Token}", ct);
        var (_, body) = await BodyAsync(ok, ct);
        var url = body.GetProperty("grants")[0].GetProperty("url").GetString()!;
        foreach (var entry in host.Factory.Logs.Entries.ToList())
        {
            var text = entry.Message + entry.Exception + string.Join(' ', entry.State.Values);
            text.ShouldNotContain("X-Amz-Signature");
        }

        var audit = await TextAsync(db, "SELECT string_agg(details::text || coalesce(reason_code, ''), ' ') FROM socalytics.security_audit_event", ct);
        audit.ShouldNotBeNull();
        audit.ShouldNotContain("X-Amz-Signature");
        audit.ShouldNotContain(url);
    }

    [Fact]
    public async Task RefusesJobsWithoutPinnedMediaAsNotApplicableAndAuditsThem()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, host) = await StartAsync(ct);
        await using var _ = host;
        var seed = await SeedAsync(db, ct, pinned: false);
        using var m = new TestManagerClient(host.Factory);
        var c = await ClaimAsync(seed, m, ct);

        using var response = await m.GetSegmentMediaAsync(c.Attempt, $"fencingToken={c.Token}", ct);
        var (status, body) = await BodyAsync(response, ct);
        status.ShouldBe(HttpStatusCode.Conflict);
        body.GetProperty("code").GetString().ShouldBe("segment-media-not-applicable");
        body.ToString().ShouldNotContain("segments/");

        (await TextAsync(db, "SELECT string_agg(reason_code, ',') FROM socalytics.security_audit_event WHERE event_type = 'analysis.segment-media-read-refused'", ct))
            .ShouldBe("out-of-scope");
    }

    [Fact]
    public async Task RefusesMediaWhoseAttestedChecksumDiffersFromThePin()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, host) = await StartAsync(ct);
        await using var _ = host;
        var seed = await SeedAsync(db, ct, tamperedPin: true);
        using var m = new TestManagerClient(host.Factory);
        var c = await ClaimAsync(seed, m, ct);

        using var response = await m.GetSegmentMediaAsync(c.Attempt, $"fencingToken={c.Token}", ct);
        var (status, body) = await BodyAsync(response, ct);
        status.ShouldBe(HttpStatusCode.Conflict, body.ToString());
        body.GetProperty("code").GetString().ShouldBe("segment-media-invalid");
    }
}
