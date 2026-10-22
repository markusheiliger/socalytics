using Amazon.S3.Model;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
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
using Npgsql;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Segments;

public sealed class SegmentFailureTests(PostgresContainerFixture postgres, RustFsContainerFixture rustfs) : IDisposable
{
    private const string SourceKey = "sources/segment-failure-source.mp4";
    private const int MaxAttempts = 3;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(30);
    private static readonly string MappingDigest = "sha-256:" + new string('a', 64);
    private static readonly string PolicyDigest = "sha-256:" + new string('b', 64);

    private readonly SyntheticMedia media = SyntheticMedia.Create();
    private readonly string workingRoot = Path.Combine(Path.GetTempPath(), $"socalytics-failure-tests-{Guid.NewGuid():N}");

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

    // Throws the queued exceptions one per call, then delegates to the real extractor.
    private sealed class ScriptedExtractor(ISegmentExtractor inner) : ISegmentExtractor
    {
        public Queue<Exception> Failures { get; } = new();

        public int Calls { get; private set; }

        public Task<ExtractedSegment> ExtractAsync(Uri sourceReadUrl, SegmentInterval requestedSourceInterval, CancellationToken cancellationToken)
        {
            Calls++;
            return Failures.TryDequeue(out var failure)
                ? throw failure
                : inner.ExtractAsync(sourceReadUrl, requestedSourceInterval, cancellationToken);
        }
    }

    // Fails every object write while FailPuts is set; everything else goes to the real store.
    private sealed class FlakyStorage(IObjectStorage inner) : IObjectStorage
    {
        public bool FailPuts { get; set; }

        public Task PutObjectAsync(
            string objectKey, string contentType, Stream content, long contentLength, Sha256Digest digest, CancellationToken cancellationToken) =>
            FailPuts
                ? throw new IOException("The object store is unavailable.")
                : inner.PutObjectAsync(objectKey, contentType, content, contentLength, digest, cancellationToken);

        public Task<MultipartUploadReference> InitiateCompositeMultipartUploadAsync(string objectKey, string contentType, CancellationToken cancellationToken) =>
            inner.InitiateCompositeMultipartUploadAsync(objectKey, contentType, cancellationToken);

        public PartUploadGrant PresignUploadPart(MultipartUploadReference upload, int partNumber, long contentLength, Sha256Digest partDigest, DateTimeOffset expiresAt) =>
            inner.PresignUploadPart(upload, partNumber, contentLength, partDigest, expiresAt);

        public Task<IReadOnlyList<StoredPart>> ListPartsAsync(MultipartUploadReference upload, int? maxParts, CancellationToken cancellationToken) =>
            inner.ListPartsAsync(upload, maxParts, cancellationToken);

        public Task CompleteMultipartUploadAsync(
            MultipartUploadReference upload, IReadOnlyList<CompletedPartEntry> parts, CompositeContentDigest contentDigest,
            long totalSizeBytes, CancellationToken cancellationToken) =>
            inner.CompleteMultipartUploadAsync(upload, parts, contentDigest, totalSizeBytes, cancellationToken);

        public Task<CompositeIntegrityEvidence?> GetIntegrityEvidenceAsync(string objectKey, CancellationToken cancellationToken) =>
            inner.GetIntegrityEvidenceAsync(objectKey, cancellationToken);

        public ObjectUploadGrant PresignPutObject(string objectKey, long contentLength, Sha256Digest digest, DateTimeOffset expiresAt) =>
            inner.PresignPutObject(objectKey, contentLength, digest, expiresAt);

        public ObjectReadGrant PresignGetObject(string objectKey, DateTimeOffset expiresAt) =>
            inner.PresignGetObject(objectKey, expiresAt);

        public Task<ObjectChecksum?> GetObjectChecksumAsync(string objectKey, CancellationToken cancellationToken) =>
            inner.GetObjectChecksumAsync(objectKey, cancellationToken);

        public Task<ObjectReadResult?> ReadObjectAsync(string objectKey, long maxBytes, CancellationToken cancellationToken) =>
            inner.ReadObjectAsync(objectKey, maxBytes, cancellationToken);

        public Task AbortMultipartUploadAsync(MultipartUploadReference upload, CancellationToken cancellationToken) =>
            inner.AbortMultipartUploadAsync(upload, cancellationToken);

        public Task DeleteObjectAsync(string objectKey, CancellationToken cancellationToken) =>
            inner.DeleteObjectAsync(objectKey, cancellationToken);
    }

    private sealed class Harness(
        IsolatedDatabase db, ServiceProvider provider, MutableTimeProvider time, FlakyStorage storage,
        ScriptedExtractor extractor, ISegmentSourceLookup sources, string workingRoot) : IAsyncDisposable
    {
        public MutableTimeProvider Time => time;

        public ScriptedExtractor Extractor => extractor;

        public FlakyStorage Storage => storage;

        public Guid RecordingVersionId { get; } = Guid.NewGuid();

        public MaterializedSegmentIdentity Identity(int segmentNumber = 1) =>
            new(RecordingVersionId, Sha256Digest.Parse(MappingDigest), Sha256Digest.Parse(PolicyDigest), segmentNumber);

        public async Task<T> WithHandlerAsync<T>(Func<MaterializeSegmentHandler, ISegmentStore, IUnitOfWork, Task<T>> action)
        {
            await using var scope = provider.CreateAsyncScope();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var store = new SegmentStore(scope.ServiceProvider.GetRequiredService<IDbSession>(), time);
            var handler = new MaterializeSegmentHandler(
                unitOfWork, store, sources, extractor, storage, new SegmentManifestBuilder(),
                Options.Create(new SegmentMaterializationOptions
                {
                    WorkingDirectory = workingRoot,
                    MaxAttempts = MaxAttempts,
                    RetryDelay = RetryDelay,
                    ExecutionTimeout = TimeSpan.FromSeconds(60),
                }),
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

        public Task<IReadOnlyList<SegmentMaterializationOutcome>> ProcessDueAsync(CancellationToken ct) =>
            WithHandlerAsync((handler, _, _) => handler.ProcessDueAsync("worker-1", 10, ct));

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

        var realStorage = rustfs.CreateObjectStorage();
        var bytes = media.ReadBytes();
        await using (var content = new MemoryStream(bytes))
        {
            await realStorage.PutObjectAsync(
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
        var extractor = new ScriptedExtractor(new FfmpegSegmentExtractor(
            new ToolchainProbe(toolchain), toolchain, materialization, NullLogger<FfmpegSegmentExtractor>.Instance));

        // Media 0-10 s maps 1:1 to match time 0-10 s.
        var sources = new FakeSources(new SegmentSource(Guid.NewGuid(), SourceKey, [new TimelineSpan(0, 10_000, 0)]));
        var harness = new Harness(db, provider, time, new FlakyStorage(realStorage), extractor, sources, workingRoot);
        await harness.ExecSuperuserAsync(
            "ALTER TABLE socalytics.materialized_segments DROP CONSTRAINT materialized_segments_recording_version_id_fkey", ct);
        return harness;
    }

    private async Task<List<string>> ListKeysAsync(MaterializedSegmentIdentity identity, CancellationToken ct)
    {
        var prefix = MaterializeSegmentHandler.CandidatePrefix(identity, Guid.Empty)[..^37];
        using var client = rustfs.CreateS3Client();
        var response = await client.ListObjectsV2Async(
            new ListObjectsV2Request { BucketName = rustfs.Bucket, Prefix = prefix }, ct);
        return [.. (response.S3Objects ?? []).Select(o => o.Key)];
    }

    // Arranges the transient failure of the given reason for every attempt.
    private static void FailEveryAttempt(Harness h, string reason)
    {
        if (reason == SegmentFailureReasons.StorageUnavailable)
        {
            h.Storage.FailPuts = true;
            return;
        }

        for (var i = 0; i < MaxAttempts; i++)
        {
            h.Extractor.Failures.Enqueue(new SegmentExtractionException(reason, transient: true));
        }
    }

    [Theory]
    [InlineData(SegmentFailureReasons.SourceUnavailable)]
    [InlineData(SegmentFailureReasons.StorageUnavailable)]
    [InlineData(SegmentFailureReasons.ExtractionFailed)]
    public async Task A_transient_failure_returns_the_row_to_demanded_absent_until_the_budget_is_exhausted(string reason)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await CreateAsync(ct);
        var identity = h.Identity();
        await h.DemandAsync(identity, ct);
        FailEveryAttempt(h, reason);

        for (var attempt = 1; attempt < MaxAttempts; attempt++)
        {
            var started = h.Time.GetUtcNow();
            var outcome = (await h.ProcessDueAsync(ct)).ShouldHaveSingleItem();

            outcome.Result.ShouldBe(SegmentMaterializationResult.RetryScheduled);
            outcome.FailureReason.ShouldBe(reason);
            var row = await h.FindAsync(identity, ct);
            row.State.ShouldBe(MaterializedSegmentState.Absent);
            row.Demanded.ShouldBeTrue();
            row.AttemptsUsed.ShouldBe(attempt);
            row.CandidateId.ShouldBeNull();
            row.Association.ShouldBeNull();
            row.NextAttemptAt.ShouldNotBeNull().ShouldBe(started + RetryDelay, TimeSpan.FromMilliseconds(1));
            (await ListKeysAsync(identity, ct)).ShouldBeEmpty();

            // Nothing is due before the retry delay has passed.
            (await h.ProcessDueAsync(ct)).ShouldBeEmpty();
            h.Time.Set(h.Time.GetUtcNow() + RetryDelay);
        }

        var last = (await h.ProcessDueAsync(ct)).ShouldHaveSingleItem();

        last.Result.ShouldBe(SegmentMaterializationResult.Failed);
        last.FailureReason.ShouldBe(SegmentFailureReasons.RetryBudgetExhausted);
        var failed = await h.FindAsync(identity, ct);
        failed.State.ShouldBe(MaterializedSegmentState.Failed);
        failed.FailureReason.ShouldBe(SegmentFailureReasons.RetryBudgetExhausted);
        failed.AttemptsUsed.ShouldBe(MaxAttempts);
        failed.NextAttemptAt.ShouldBeNull();
        failed.Association.ShouldBeNull();
        (await ListKeysAsync(identity, ct)).ShouldBeEmpty();
        h.Time.Set(h.Time.GetUtcNow() + RetryDelay);
        (await h.ProcessDueAsync(ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_retry_that_succeeds_publishes_and_leaves_one_candidate()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await CreateAsync(ct);
        var identity = h.Identity();
        await h.DemandAsync(identity, ct);
        h.Storage.FailPuts = true;

        (await h.ProcessDueAsync(ct)).ShouldHaveSingleItem().Result.ShouldBe(SegmentMaterializationResult.RetryScheduled);
        (await ListKeysAsync(identity, ct)).ShouldBeEmpty();

        h.Storage.FailPuts = false;
        h.Time.Set(h.Time.GetUtcNow() + RetryDelay);
        (await h.ProcessDueAsync(ct)).ShouldHaveSingleItem().Result.ShouldBe(SegmentMaterializationResult.Published);

        var row = await h.FindAsync(identity, ct);
        row.State.ShouldBe(MaterializedSegmentState.Available);
        row.AttemptsUsed.ShouldBe(2);
        (await ListKeysAsync(identity, ct)).Order().ShouldBe(
            [row.Association!.ManifestObjectKey, row.Association.MediaObjectKey]);
    }

    [Theory]
    [InlineData(SegmentFailureReasons.ToolchainUnavailable)]
    [InlineData(SegmentFailureReasons.TooLarge)]
    [InlineData(SegmentFailureReasons.CoverageIncomplete)]
    [InlineData(SegmentFailureReasons.MediaInvalid)]
    public async Task A_non_retryable_reason_ends_failed_on_the_first_attempt(string reason)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await CreateAsync(ct);
        var identity = h.Identity();
        await h.DemandAsync(identity, ct);
        h.Extractor.Failures.Enqueue(new SegmentExtractionException(reason, transient: false));

        var outcome = (await h.ProcessDueAsync(ct)).ShouldHaveSingleItem();

        outcome.Result.ShouldBe(SegmentMaterializationResult.Failed);
        outcome.FailureReason.ShouldBe(reason);
        var row = await h.FindAsync(identity, ct);
        row.State.ShouldBe(MaterializedSegmentState.Failed);
        row.FailureReason.ShouldBe(reason);
        row.AttemptsUsed.ShouldBe(1);
        row.NextAttemptAt.ShouldBeNull();
        (await ListKeysAsync(identity, ct)).ShouldBeEmpty();
        h.Time.Set(h.Time.GetUtcNow() + RetryDelay);
        (await h.ProcessDueAsync(ct)).ShouldBeEmpty();
        h.Extractor.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task A_new_demand_for_a_failed_identity_starts_a_new_cycle()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await CreateAsync(ct);
        var identity = h.Identity();
        await h.DemandAsync(identity, ct);
        h.Extractor.Failures.Enqueue(new SegmentExtractionException(SegmentFailureReasons.TooLarge, transient: false));
        (await h.ProcessDueAsync(ct)).ShouldHaveSingleItem().Result.ShouldBe(SegmentMaterializationResult.Failed);
        var failed = await h.FindAsync(identity, ct);

        var again = await h.DemandAsync(identity, ct);

        again.Id.ShouldBe(failed.Id);
        again.State.ShouldBe(MaterializedSegmentState.Absent);
        again.Demanded.ShouldBeTrue();
        again.AttemptsUsed.ShouldBe(0);
        again.FailureReason.ShouldBeNull();
        again.NextAttemptAt.ShouldBeNull();

        (await h.ProcessDueAsync(ct)).ShouldHaveSingleItem().Result.ShouldBe(SegmentMaterializationResult.Published);
        var row = await h.FindAsync(identity, ct);
        row.State.ShouldBe(MaterializedSegmentState.Available);
        row.AttemptsUsed.ShouldBe(1);
    }
}
