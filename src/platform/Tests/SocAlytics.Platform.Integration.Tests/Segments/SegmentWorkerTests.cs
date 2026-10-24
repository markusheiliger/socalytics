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
using SocAlytics.Platform.Infrastructure.Workers;
using SocAlytics.Platform.Integration.Tests.IdentityAccess.Support;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Integration.Tests.Recordings;
using SocAlytics.Platform.Migrator;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Segments;

public sealed class SegmentWorkerTests(PostgresContainerFixture postgres, RustFsContainerFixture rustfs) : IDisposable
{
    private const string SourceKey = "sources/segment-worker-source.mp4";
    private static readonly string MappingDigest = "sha-256:" + new string('a', 64);
    private static readonly string PolicyDigest = "sha-256:" + new string('b', 64);

    private readonly SyntheticMedia media = SyntheticMedia.Create();
    private readonly string workingRoot = Path.Combine(Path.GetTempPath(), $"socalytics-worker-tests-{Guid.NewGuid():N}");

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

    // Owns a scope of the platform provider for the lifetime of one worker scope.
    private sealed class PlatformScope(ServiceProvider platform) : IAsyncDisposable
    {
        private readonly AsyncServiceScope scope = platform.CreateAsyncScope();

        public IServiceProvider Services => scope.ServiceProvider;

        public ValueTask DisposeAsync() => scope.DisposeAsync();
    }

    private sealed class Harness(
        IsolatedDatabase db, ServiceProvider platform, ServiceProvider workerServices, MutableTimeProvider time,
        IObjectStorage storage, SegmentMaterializationWorker worker) : IAsyncDisposable
    {
        public IObjectStorage Storage => storage;

        public MutableTimeProvider Time => time;

        public SegmentMaterializationWorker Worker => worker;

        public Guid RecordingVersionId { get; } = Guid.NewGuid();

        public MaterializedSegmentIdentity Identity(int segmentNumber) =>
            new(RecordingVersionId, Sha256Digest.Parse(MappingDigest), Sha256Digest.Parse(PolicyDigest), segmentNumber);

        public async Task<T> WithStoreAsync<T>(Func<ISegmentStore, IUnitOfWork, Task<T>> action)
        {
            await using var scope = platform.CreateAsyncScope();
            var store = new SegmentStore(scope.ServiceProvider.GetRequiredService<IDbSession>(), time);
            return await action(store, scope.ServiceProvider.GetRequiredService<IUnitOfWork>());
        }

        public Task DemandAsync(MaterializedSegmentIdentity identity, CancellationToken ct) =>
            WithStoreAsync(async (store, uow) =>
            {
                await using var tx = await uow.BeginAsync(ct);
                await store.DemandAsync(identity, 1, 5, ct);
                await tx.CommitAsync(ct);
                return 0;
            });

        public Task<MaterializedSegmentRecord> FindAsync(MaterializedSegmentIdentity identity, CancellationToken ct) =>
            WithStoreAsync(async (store, _) => (await store.FindAsync(identity, ct))!);

        public Task<ClaimedSegment> ClaimAsync(string owner, CancellationToken ct) =>
            WithStoreAsync(async (store, uow) =>
            {
                await using var tx = await uow.BeginAsync(ct);
                var claimed = await store.ClaimDueAsync(owner, TimeSpan.FromMinutes(5), 1, ct);
                await tx.CommitAsync(ct);
                return claimed.Single();
            });

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
            await workerServices.DisposeAsync();
            await platform.DisposeAsync();
            await db.DisposeAsync();
        }
    }

    private async Task<Harness> CreateAsync(CancellationToken ct)
    {
        var db = await postgres.CreateDatabaseAsync(ct);
        (await MigratorHarness.RunAsync(db, TestMigrationCatalogs.Platform(), ct)).ExitCode
            .ShouldBe(MigratorExitCode.Success);
        var time = new MutableTimeProvider(DateTimeOffset.UtcNow);
        var platform = PlatformServices.Build(db, time: time);

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
            MaxConcurrentPerInstance = 2,
        });
        var probe = new ToolchainProbe(toolchain);
        var extractor = new FfmpegSegmentExtractor(probe, toolchain, materialization, NullLogger<FfmpegSegmentExtractor>.Instance);
        var sources = new FakeSources(new SegmentSource(Guid.NewGuid(), SourceKey, [new TimelineSpan(0, 10_000, 0)]));

        var workerServices = new ServiceCollection()
            .AddScoped(_ => new PlatformScope(platform))
            .AddScoped(sp =>
            {
                var scope = sp.GetRequiredService<PlatformScope>().Services;
                return new MaterializeSegmentHandler(
                    scope.GetRequiredService<IUnitOfWork>(),
                    new SegmentStore(scope.GetRequiredService<IDbSession>(), time),
                    sources, extractor, storage, new SegmentManifestBuilder(), materialization, time,
                    NullLogger<MaterializeSegmentHandler>.Instance);
            })
            .BuildServiceProvider();
        var worker = new SegmentMaterializationWorker(
            workerServices, materialization, Options.Create(new AnalysisWorkerOptions()), probe, time,
            NullLogger<SegmentMaterializationWorker>.Instance);

        var harness = new Harness(db, platform, workerServices, time, storage, worker);
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
    public async Task The_worker_processes_every_demanded_row()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await CreateAsync(ct);
        var identities = new[] { h.Identity(1), h.Identity(2) };
        foreach (var identity in identities)
        {
            await h.DemandAsync(identity, ct);
        }

        await h.Worker.RunOnceAsync(ct);

        foreach (var identity in identities)
        {
            var row = await h.FindAsync(identity, ct);
            row.State.ShouldBe(MaterializedSegmentState.Available);
            row.Association.ShouldNotBeNull();
        }
    }

    [Fact]
    public async Task An_expired_materializing_lease_is_reclaimed_after_a_restart_without_publishing_the_old_candidate()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await CreateAsync(ct);
        var identity = h.Identity(1);
        await h.DemandAsync(identity, ct);

        // A worker claims, stores a candidate object, and dies before publishing.
        var dead = await h.ClaimAsync("dead-instance:0", ct);
        var oldCandidate = dead.Segment.CandidateId!.Value;
        var oldKey = MaterializeSegmentHandler.CandidatePrefix(identity, oldCandidate) + MaterializeSegmentHandler.MediaFileName;
        var junk = new byte[] { 1, 2, 3 };
        await using (var content = new MemoryStream(junk))
        {
            await h.Storage.PutObjectAsync(
                oldKey, "video/mp4", content, junk.Length,
                Sha256Digest.FromBytes(System.Security.Cryptography.SHA256.HashData(junk)), ct);
        }

        // Before expiry the new instance leaves the row alone.
        await h.Worker.RunOnceAsync(ct);
        (await h.FindAsync(identity, ct)).State.ShouldBe(MaterializedSegmentState.Materializing);

        h.Time.Set(h.Time.GetUtcNow() + TimeSpan.FromMinutes(6));
        await h.Worker.RunOnceAsync(ct);

        var row = await h.FindAsync(identity, ct);
        row.State.ShouldBe(MaterializedSegmentState.Available);
        row.CandidateId.ShouldNotBe(oldCandidate);
        row.Association!.MediaObjectKey.ShouldNotContain(oldCandidate.ToString("D"));
        (await ListKeysAsync(SegmentPrefix(identity), ct)).Order().ShouldBe(
            [row.Association.ManifestObjectKey, row.Association.MediaObjectKey]);
    }
}
