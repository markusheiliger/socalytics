using System.Text.Json;
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
using SocAlytics.Platform.Infrastructure.Segments;
using SocAlytics.Platform.Integration.Tests.IdentityAccess.Support;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Integration.Tests.Recordings;
using SocAlytics.Platform.Migrator;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Segments;

public sealed class SegmentMaterializationTests(PostgresContainerFixture postgres, RustFsContainerFixture rustfs) : IDisposable
{
    private const string SourceKey = "sources/segment-materialization-source.mp4";
    private static readonly string MappingDigest = "sha-256:" + new string('a', 64);
    private static readonly string PolicyDigest = "sha-256:" + new string('b', 64);

    private readonly SyntheticMedia media = SyntheticMedia.Create();
    private readonly string workingRoot = Path.Combine(Path.GetTempPath(), $"socalytics-materialization-tests-{Guid.NewGuid():N}");

    public void Dispose()
    {
        media.Dispose();
        if (Directory.Exists(workingRoot))
        {
            Directory.Delete(workingRoot, recursive: true);
        }
    }

    private sealed class FakeSources(SegmentSource source) : ISegmentSourceLookup
    {
        public Task<SegmentSource?> FindAsync(Guid recordingVersionId, string timelineMappingDigest, CancellationToken cancellationToken) =>
            Task.FromResult<SegmentSource?>(source);
    }

    private sealed class Harness(
        IsolatedDatabase db, ServiceProvider provider, MutableTimeProvider time, IObjectStorage storage,
        ISegmentSourceLookup sources, FfmpegSegmentExtractor extractor, string workingRoot) : IAsyncDisposable
    {
        public IObjectStorage Storage => storage;

        public MutableTimeProvider Time => time;

        public Guid RecordingVersionId { get; } = Guid.NewGuid();

        public MaterializedSegmentIdentity Identity(int segmentNumber = 1, string? policyDigest = null) =>
            new(RecordingVersionId, Sha256Digest.Parse(MappingDigest), Sha256Digest.Parse(policyDigest ?? PolicyDigest), segmentNumber);

        // Each call gets its own scope, connection, and handler like concurrent worker iterations do.
        public async Task<T> WithHandlerAsync<T>(Func<MaterializeSegmentHandler, ISegmentStore, IUnitOfWork, Task<T>> action)
        {
            await using var scope = provider.CreateAsyncScope();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var store = new SegmentStore(scope.ServiceProvider.GetRequiredService<IDbSession>(), time);
            var handler = new MaterializeSegmentHandler(
                unitOfWork, store, sources, extractor, storage, new SegmentManifestBuilder(),
                Options.Create(new SegmentMaterializationOptions { WorkingDirectory = workingRoot, MaxAttempts = 3 }),
                time, NullLogger<MaterializeSegmentHandler>.Instance);
            return await action(handler, store, unitOfWork);
        }

        public Task<MaterializedSegmentRecord> DemandAsync(MaterializedSegmentIdentity identity, CancellationToken ct) =>
            WithHandlerAsync(async (_, store, uow) =>
            {
                await using var tx = await uow.BeginAsync(ct);
                var row = await store.DemandAsync(identity, 1, 5, ct);
                await tx.CommitAsync(ct);
                return row;
            });

        public Task<MaterializedSegmentRecord> FindAsync(MaterializedSegmentIdentity identity, CancellationToken ct) =>
            WithHandlerAsync(async (_, store, _) => (await store.FindAsync(identity, ct))!);

        public Task<IReadOnlyList<SegmentMaterializationOutcome>> ProcessDueAsync(string owner, CancellationToken ct) =>
            WithHandlerAsync((handler, _, _) => handler.ProcessDueAsync(owner, 10, ct));

        public async Task<long> ScalarAsync(string sql, CancellationToken ct)
        {
            await using var connection = new NpgsqlConnection(db.AppConnectionString);
            await connection.OpenAsync(ct);
            await using var command = new NpgsqlCommand(sql, connection);
            return (long)(await command.ExecuteScalarAsync(ct))!;
        }

        public async Task ExecSuperuserAsync(string sql, CancellationToken ct)
        {
            var connectionString = new NpgsqlConnectionStringBuilder(db.SuperuserConnectionString) { Database = db.Name }.ConnectionString;
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(ct);
            await using var command = new NpgsqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync(ct);
        }

        public async ValueTask DisposeAsync()
        {
            await provider.DisposeAsync();
            await db.DisposeAsync();
        }
    }

    private async Task<Harness> CreateAsync(CancellationToken ct)
    {
        var db = await postgres.CreateDatabaseAsync(ct);
        (await MigratorHarness.RunAsync(db, TestMigrationCatalogs.Platform(), ct)).ExitCode
            .ShouldBe(MigratorExitCode.Success);
        var time = new MutableTimeProvider(DateTimeOffset.UtcNow);
        var provider = PlatformServices.Build(db, time: time);

        var storage = rustfs.CreateObjectStorage();
        var bytes = media.ReadBytes();
        await using (var content = new MemoryStream(bytes))
        {
            await storage.PutObjectAsync(
                SourceKey, "video/mp4", content, bytes.Length,
                Sha256Digest.FromBytes(System.Security.Cryptography.SHA256.HashData(bytes)), ct);
        }

        var toolchain = Options.Create(new SegmentToolchainOptions
        {
            FfmpegPath = SyntheticMedia.FfmpegPath,
            FfprobePath = Path.Combine(Path.GetDirectoryName(SyntheticMedia.FfmpegPath) ?? string.Empty, "ffprobe"),
        });
        var materialization = Options.Create(new SegmentMaterializationOptions
        {
            WorkingDirectory = workingRoot,
            ExecutionTimeout = TimeSpan.FromSeconds(60),
            MaxSegmentBytes = 8 * 1024 * 1024,
        });
        var extractor = new FfmpegSegmentExtractor(
            new ToolchainProbe(toolchain), toolchain, materialization, NullLogger<FfmpegSegmentExtractor>.Instance);

        // Media 0-10 s maps 1:1 to match time 0-10 s.
        var sources = new FakeSources(new SegmentSource(Guid.NewGuid(), SourceKey, [new TimelineSpan(0, 10_000, 0)]));
        var harness = new Harness(db, provider, time, storage, sources, extractor, workingRoot);
        // The tests use a synthetic recording version that the source fake describes.
        await harness.ExecSuperuserAsync(
            "ALTER TABLE socalytics.materialized_segments DROP CONSTRAINT materialized_segments_recording_version_id_fkey", ct);
        return harness;
    }

    private async Task<List<string>> ListKeysAsync(string prefix, CancellationToken ct)
    {
        using var client = rustfs.CreateS3Client();
        var response = await client.ListObjectsV2Async(
            new ListObjectsV2Request { BucketName = rustfs.Bucket, Prefix = prefix }, ct);
        return [.. (response.S3Objects ?? []).Select(o => o.Key)];
    }

    private static string SegmentPrefix(MaterializedSegmentIdentity identity) =>
        MaterializeSegmentHandler.CandidatePrefix(identity, Guid.Empty)[..^37];

    [Fact]
    public async Task Materialization_publishes_a_manifest_that_matches_the_stored_media()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await CreateAsync(ct);
        var identity = h.Identity(segmentNumber: 2);
        await h.DemandAsync(identity, ct);

        var outcomes = await h.ProcessDueAsync("worker-1", ct);

        outcomes.ShouldHaveSingleItem().Result.ShouldBe(SegmentMaterializationResult.Published);
        var row = await h.FindAsync(identity, ct);
        row.State.ShouldBe(MaterializedSegmentState.Available);
        var association = row.Association!;
        association.MediaObjectKey.ShouldStartWith(
            $"segments/{identity.RecordingVersionId:D}/{new string('a', 64)}/{new string('b', 64)}/2/");
        association.MediaObjectKey.ShouldEndWith("/segment.mp4");
        association.ManifestObjectKey.ShouldEndWith("/segment-manifest.json");

        var mediaChecksum = (await h.Storage.GetObjectChecksumAsync(association.MediaObjectKey, ct))!;
        mediaChecksum.Digest.ToString().ShouldBe(association.MediaDigest);
        mediaChecksum.ContentLength.ShouldBe(association.MediaSizeBytes);
        var manifestChecksum = (await h.Storage.GetObjectChecksumAsync(association.ManifestObjectKey, ct))!;
        manifestChecksum.Digest.ToString().ShouldBe(association.ManifestChecksum);

        var manifestBytes = (await h.Storage.ReadObjectAsync(association.ManifestObjectKey, 1024 * 1024, ct))!.Content!;
        using var manifest = JsonDocument.Parse(manifestBytes);
        var root = manifest.RootElement;
        root.GetProperty("contractVersion").GetString().ShouldBe("1.0.0");
        root.GetProperty("identity").GetProperty("segmentNumber").GetInt32().ShouldBe(2);
        root.GetProperty("identity").GetProperty("timelineMappingDigest").GetString().ShouldBe(MappingDigest);
        root.GetProperty("nominalWindow").GetProperty("startMs").GetInt64().ShouldBe(5000);
        root.GetProperty("nominalWindow").GetProperty("endMs").GetInt64().ShouldBe(10_000);
        root.GetProperty("media").GetProperty("digest").GetString().ShouldBe(association.MediaDigest);
        root.GetProperty("media").GetProperty("sizeBytes").GetInt64().ShouldBe(association.MediaSizeBytes);
        root.GetProperty("media").GetProperty("mediaType").GetString().ShouldBe("video/mp4");
        var requested = root.GetProperty("requestedSourceInterval");
        var encoded = root.GetProperty("encodedInterval");
        encoded.GetProperty("startMs").GetInt64().ShouldBeLessThanOrEqualTo(requested.GetProperty("startMs").GetInt64());
        encoded.GetProperty("endMs").GetInt64().ShouldBeGreaterThanOrEqualTo(requested.GetProperty("endMs").GetInt64());
        root.GetProperty("producer").GetProperty("toolchain").GetString().ShouldStartWith("ffmpeg ");
    }

    [Fact]
    public async Task Eight_concurrent_requests_publish_one_association_and_leave_no_candidate_objects()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await CreateAsync(ct);
        var identity = h.Identity();

        var all = await Task.WhenAll(Enumerable.Range(0, 8).Select(async i =>
        {
            await h.DemandAsync(identity, ct);
            return await h.ProcessDueAsync($"worker-{i}", ct);
        }));

        all.SelectMany(o => o).Count(o => o.Result == SegmentMaterializationResult.Published).ShouldBe(1);
        (await h.ScalarAsync("SELECT count(*) FROM socalytics.materialized_segments", ct)).ShouldBe(1);
        var row = await h.FindAsync(identity, ct);
        row.State.ShouldBe(MaterializedSegmentState.Available);
        (await ListKeysAsync(SegmentPrefix(identity), ct)).Order().ShouldBe(
            [row.Association!.ManifestObjectKey, row.Association.MediaObjectKey]);
    }

    [Fact]
    public async Task A_reclaimed_candidate_that_loses_the_compare_and_set_deletes_its_objects()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await CreateAsync(ct);
        var identity = h.Identity();
        await h.DemandAsync(identity, ct);

        var first = (await h.WithHandlerAsync(async (_, store, uow) =>
        {
            await using var tx = await uow.BeginAsync(ct);
            var claimed = await store.ClaimDueAsync("old-worker", TimeSpan.FromMinutes(5), 1, ct);
            await tx.CommitAsync(ct);
            return claimed;
        })).Single();

        // The lease expires and another worker reclaims and publishes first.
        h.Time.Set(h.Time.GetUtcNow() + TimeSpan.FromMinutes(6));
        var reclaimed = await h.ProcessDueAsync("new-worker", ct);
        reclaimed.ShouldHaveSingleItem().Result.ShouldBe(SegmentMaterializationResult.Published);

        var stale = await h.WithHandlerAsync((handler, _, _) => handler.ProcessAsync(first, ct));

        stale.Result.ShouldBe(SegmentMaterializationResult.Superseded);
        var row = await h.FindAsync(identity, ct);
        (await ListKeysAsync(SegmentPrefix(identity), ct)).Order().ShouldBe(
            [row.Association!.ManifestObjectKey, row.Association.MediaObjectKey]);
    }

    [Fact]
    public async Task A_recording_set_only_revision_reuses_the_published_association()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await CreateAsync(ct);
        var identity = h.Identity();
        await h.DemandAsync(identity, ct);
        await h.ProcessDueAsync("worker-1", ct);
        var published = await h.FindAsync(identity, ct);

        // A new recording set version over the same version and mapping asks for the same identity.
        var again = await h.DemandAsync(identity, ct);

        again.Id.ShouldBe(published.Id);
        again.State.ShouldBe(MaterializedSegmentState.Available);
        again.Association.ShouldBe(published.Association);
        (await h.ProcessDueAsync("worker-2", ct)).ShouldBeEmpty();
        (await h.WithHandlerAsync((handler, _, _) => handler.VerifyAvailableAsync(again, ct))).ShouldBe(published.Association);
    }

    [Fact]
    public async Task Policy_digests_coexist_for_the_same_segment_number()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await CreateAsync(ct);
        var one = h.Identity();
        var two = h.Identity(policyDigest: "sha-256:" + new string('c', 64));
        await h.DemandAsync(one, ct);
        await h.DemandAsync(two, ct);

        var outcomes = await h.ProcessDueAsync("worker-1", ct);

        outcomes.ShouldAllBe(o => o.Result == SegmentMaterializationResult.Published);
        outcomes.Count.ShouldBe(2);
        var first = await h.FindAsync(one, ct);
        var second = await h.FindAsync(two, ct);
        first.Association!.MediaObjectKey.ShouldNotBe(second.Association!.MediaObjectKey);
        first.Association.MediaDigest.ShouldBe(second.Association.MediaDigest);
    }

    [Fact]
    public async Task A_published_association_is_a_cache_hit_only_while_its_checksum_is_attested()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await CreateAsync(ct);
        var identity = h.Identity();
        await h.DemandAsync(identity, ct);
        await h.ProcessDueAsync("worker-1", ct);
        var published = await h.FindAsync(identity, ct);

        await h.Storage.DeleteObjectAsync(published.Association!.MediaObjectKey, ct);
        var hit = await h.WithHandlerAsync((handler, _, _) => handler.VerifyAvailableAsync(published, ct));

        hit.ShouldBeNull();
        var row = await h.FindAsync(identity, ct);
        row.State.ShouldBe(MaterializedSegmentState.Failed);
        row.FailureReason.ShouldBe(SegmentFailureReasons.MediaInvalid);
        (await ListKeysAsync(SegmentPrefix(identity), ct)).ShouldBeEmpty();
    }
}
