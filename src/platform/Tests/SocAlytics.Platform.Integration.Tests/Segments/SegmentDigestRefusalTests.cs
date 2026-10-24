using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.S3.Model;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Shouldly;
using SocAlytics.Platform.Application.Abstractions.ObjectStorage;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.Segments;
using SocAlytics.Platform.Domain.Recordings;
using SocAlytics.Platform.Domain.Segments;
using SocAlytics.Platform.Infrastructure.Persistence;
using SocAlytics.Platform.Infrastructure.Persistence.Segments;
using SocAlytics.Platform.Integration.Tests.Analysis.Support;
using SocAlytics.Platform.Integration.Tests.IdentityAccess.Support;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Integration.Tests.Recordings;
using SocAlytics.Platform.Migrator;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Segments;

// An available association whose attested checksum or size no longer matches is refused, its objects are deleted, and
// its row is failed so it is never handed to a new job (FR-009).
public sealed class SegmentDigestRefusalTests(PostgresContainerFixture postgres, NatsContainerFixture nats, RustFsContainerFixture rustfs)
{
    private static readonly HttpClient Http = new();
    private static readonly byte[] Media = Encoding.UTF8.GetBytes("synthetic segment media");
    private static readonly string MappingDigest = "sha-256:" + new string('a', 64);
    private static readonly string PolicyDigest = "sha-256:" + new string('b', 64);

    private sealed record Seeded(MaterializedSegmentIdentity Identity, string Prefix, string MediaKey, string ManifestKey, string MediaDigest, string ManifestChecksum);

    private static string DigestOf(byte[] bytes) => Sha256Digest.FromBytes(SHA256.HashData(bytes)).ToString();

    private static string MediaKeyOf(string prefix) => prefix + MaterializeSegmentHandler.MediaFileName;

    private static string ManifestKeyOf(string prefix) => prefix + MaterializeSegmentHandler.ManifestFileName;

    private async Task PutAsync(string key, byte[] body, CancellationToken ct)
    {
        using var content = new MemoryStream(body);
        await rustfs.CreateObjectStorage().PutObjectAsync(
            key, "application/octet-stream", content, body.Length, Sha256Digest.FromBytes(SHA256.HashData(body)), ct);
    }

    private async Task<List<string>> ListKeysAsync(string prefix, CancellationToken ct)
    {
        using var client = rustfs.CreateS3Client();
        var response = await client.ListObjectsV2Async(new ListObjectsV2Request { BucketName = rustfs.Bucket, Prefix = prefix }, ct);
        return [.. (response.S3Objects ?? []).Select(o => o.Key)];
    }

    private static async Task ExecAsync(string connectionString, string sql, CancellationToken ct, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync(ct);
    }

    private static string SuperuserConnectionString(IsolatedDatabase db) =>
        new NpgsqlConnectionStringBuilder(db.SuperuserConnectionString) { Database = db.Name }.ConnectionString;

    private static byte[] ManifestOf(string mediaDigest) =>
        Encoding.UTF8.GetBytes(new JsonObject
        {
            ["contractVersion"] = "1.0.0",
            ["identity"] = new JsonObject
            {
                ["recordingVersionId"] = "recording-version-0001",
                ["timelineMappingDigest"] = MappingDigest,
                ["segmentationPolicyDigest"] = PolicyDigest,
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
        }.ToJsonString());

    // Stores both objects and an available row over them; the synthetic recording version has no recording_versions row.
    private async Task<Seeded> SeedAvailableAsync(IsolatedDatabase db, Guid recordingVersionId, CancellationToken ct)
    {
        var identity = new MaterializedSegmentIdentity(
            recordingVersionId, Sha256Digest.Parse(MappingDigest), Sha256Digest.Parse(PolicyDigest), 1);
        var prefix = $"segments/{recordingVersionId:D}/refusal-{Guid.NewGuid():N}/";
        var mediaDigest = DigestOf(Media);
        var manifestBytes = ManifestOf(mediaDigest);
        var checksum = DigestOf(manifestBytes);
        await PutAsync(ManifestKeyOf(prefix), manifestBytes, ct);
        await PutAsync(MediaKeyOf(prefix), Media, ct);

        var connection = SuperuserConnectionString(db);
        await ExecAsync(connection,
            "ALTER TABLE socalytics.materialized_segments DROP CONSTRAINT IF EXISTS materialized_segments_recording_version_id_fkey", ct);
        await ExecAsync(connection,
            """
            INSERT INTO socalytics.materialized_segments (
                id, recording_version_id, timeline_mapping_digest, segmentation_policy_digest, segment_number, policy_version,
                duration_seconds, state, demanded, media_object_key, manifest_object_key, media_digest, media_size_bytes,
                manifest_checksum, created_at, updated_at, available_at)
            VALUES (@id, @rec, @map, @pol, 1, 1, 10, 'available', false, @mk, @ok, @md, @size, @mc, now(), now(), now())
            """, ct,
            ("id", Guid.NewGuid()), ("rec", recordingVersionId), ("map", MappingDigest), ("pol", PolicyDigest),
            ("mk", MediaKeyOf(prefix)), ("ok", ManifestKeyOf(prefix)), ("md", mediaDigest), ("size", (long)Media.Length), ("mc", checksum));
        return new Seeded(identity, prefix, MediaKeyOf(prefix), ManifestKeyOf(prefix), mediaDigest, checksum);
    }

    private async Task<(IsolatedDatabase Db, ServiceProvider Provider)> CreateAsync(CancellationToken ct)
    {
        var db = await postgres.CreateDatabaseAsync(ct);
        (await MigratorHarness.RunAsync(db, TestMigrationCatalogs.Platform(), ct)).ExitCode.ShouldBe(MigratorExitCode.Success);
        return (db, PlatformServices.Build(db, time: new MutableTimeProvider(DateTimeOffset.UtcNow)));
    }

    private async Task<T> WithHandlerAsync<T>(
        ServiceProvider provider, Func<MaterializeSegmentHandler, ISegmentStore, Task<T>> action)
    {
        await using var scope = provider.CreateAsyncScope();
        var store = new SegmentStore(scope.ServiceProvider.GetRequiredService<IDbSession>(), TimeProvider.System);
        var handler = new MaterializeSegmentHandler(
            scope.ServiceProvider.GetRequiredService<IUnitOfWork>(), store, null!, null!, rustfs.CreateObjectStorage(),
            new SegmentManifestBuilder(), Options.Create(new SegmentMaterializationOptions()),
            TimeProvider.System, NullLogger<MaterializeSegmentHandler>.Instance);
        return await action(handler, store);
    }

    public static TheoryData<string> Tampering => new() { "digest", "size", "manifest" };

    [Theory]
    [MemberData(nameof(Tampering))]
    public async Task A_cache_hit_whose_attestation_differs_is_refused_and_the_row_failed(string tampering)
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, provider) = await CreateAsync(ct);
        await using var _ = provider;
        await using var __ = db;
        var seed = await SeedAvailableAsync(db, Guid.NewGuid(), ct);
        var storage = rustfs.CreateObjectStorage();
        switch (tampering)
        {
            case "digest":
                // Same size, different bytes.
                await PutAsync(seed.MediaKey, Encoding.UTF8.GetBytes("SYNTHETIC SEGMENT MEDIA"), ct);
                break;
            case "size":
                await PutAsync(seed.MediaKey, [.. Media, .. Media], ct);
                break;
            default:
                await PutAsync(seed.ManifestKey, Encoding.UTF8.GetBytes("{}"), ct);
                break;
        }

        var row = await WithHandlerAsync(provider, (_, store) => store.FindAsync(seed.Identity, ct));
        row!.State.ShouldBe(MaterializedSegmentState.Available);
        var hit = await WithHandlerAsync(provider, (handler, _) => handler.VerifyAvailableAsync(row, ct));

        hit.ShouldBeNull();
        var failed = await WithHandlerAsync(provider, (_, store) => store.FindAsync(seed.Identity, ct));
        failed!.State.ShouldBe(MaterializedSegmentState.Failed);
        failed.FailureReason.ShouldBe(SegmentFailureReasons.MediaInvalid);
        failed.Association.ShouldBeNull();
        (await ListKeysAsync(seed.Prefix, ct)).ShouldBeEmpty();
        (await storage.GetObjectChecksumAsync(seed.MediaKey, ct)).ShouldBeNull();

        // A failed row is not a cache hit for any later request.
        (await WithHandlerAsync(provider, (handler, _) => handler.VerifyAvailableAsync(failed, ct))).ShouldBeNull();
    }

    [Fact]
    public async Task A_media_read_whose_object_changed_is_refused_and_the_row_failed()
    {
        var ct = TestContext.Current.CancellationToken;
        var db = await postgres.CreateDatabaseAsync(ct);
        (await MigratorHarness.RunAsync(db, TestMigrationCatalogs.Platform(), ct)).ExitCode.ShouldBe(MigratorExitCode.Success);
        var host = new AnalysisTestHost(db, nats, rustfs, managerAuthentication: ManagerAuthentication.Test);
        host.Time.SetUtcNow(DateTimeOffset.UtcNow);
        await host.StartAsync(ct);
        await using var _ = host;

        var recording = Guid.NewGuid();
        var seed = await SeedAvailableAsync(db, recording, ct);
        Guid run = Guid.NewGuid(), node = Guid.NewGuid(), job = Guid.NewGuid(), occ = Guid.NewGuid();
        var pin = JsonSerializer.Serialize(new
        {
            VideoPolicy = "required",
            ManifestChecksum = seed.ManifestChecksum,
            ManifestReference = seed.ManifestKey,
            MediaDigest = seed.MediaDigest,
            MediaSizeBytes = (long)Media.Length,
        });
        await ExecAsync(db.MigratorConnectionString,
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
            VALUES (@job, @run, @node, 'segment', @rec, @map, @pol, 1, 'Waiting', @at);
            INSERT INTO socalytics.analysis_logical_job_snapshots (logical_job_id, max_attempts, lease_seconds, stale_timeout_seconds, execution_timeout_seconds, analyst_profile_digest, image_digest, model_digest, resources, schema_version)
            VALUES (@job, 2, 30, 90, 600, 'sha-256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', 'sha-256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', 'sha-256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', '{}'::jsonb, '1.0.0');
            INSERT INTO socalytics.analysis_logical_job_inputs (logical_job_id, inputs, recorded_at, segment_media) VALUES (@job, '[]'::jsonb, @at, @pin::jsonb);
            INSERT INTO socalytics.analysis_readiness_occurrences (id, logical_job_id, sequence, cause, created_at) VALUES (@occ, @job, 1, 'initial', @at);
            UPDATE socalytics.analysis_logical_jobs SET state = 'Ready', current_readiness_occurrence_id = @occ WHERE id = @job;
            """, ct,
            ("run", run), ("node", node), ("job", job), ("occ", occ), ("g", Guid.NewGuid()), ("rec", recording),
            ("map", MappingDigest), ("pol", PolicyDigest), ("at", DateTimeOffset.UtcNow), ("pin", pin));

        using var m = new TestManagerClient(host.Factory);
        using var claim = await m.ClaimAsync(job, occ, "key-" + job, ct: ct);
        claim.StatusCode.ShouldBe(HttpStatusCode.Created);
        var attempt = JsonDocument.Parse(await claim.Content.ReadAsStringAsync(ct)).RootElement;
        var attemptId = attempt.GetProperty("attemptId").GetGuid();
        var token = attempt.GetProperty("fencingToken").GetInt64();

        await PutAsync(seed.MediaKey, Encoding.UTF8.GetBytes("changed after publication"), ct);
        using var response = await m.GetSegmentMediaAsync(attemptId, $"fencingToken={token}", ct);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct)).RootElement;

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict, body.ToString());
        body.GetProperty("code").GetString().ShouldBe("segment-media-invalid");
        body.ToString().ShouldNotContain("segments/");
        await using var scope = host.Services.CreateAsyncScope();
        var row = await new SegmentStore(scope.ServiceProvider.GetRequiredService<IDbSession>(), TimeProvider.System)
            .FindAsync(seed.Identity, ct);
        row!.State.ShouldBe(MaterializedSegmentState.Failed);
        row.FailureReason.ShouldBe(SegmentFailureReasons.MediaInvalid);
        (await ListKeysAsync(seed.Prefix, ct)).ShouldBeEmpty();
    }
}
