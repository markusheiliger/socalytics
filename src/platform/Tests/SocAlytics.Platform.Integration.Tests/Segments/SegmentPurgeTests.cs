using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.ObjectStorage;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.Recordings;
using SocAlytics.Platform.Application.Segments;
using SocAlytics.Platform.Domain.Recordings;
using SocAlytics.Platform.Domain.Segments;
using SocAlytics.Platform.Integration.Tests.IdentityAccess.Support;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Integration.Tests.Recordings;
using SocAlytics.Platform.Migrator;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Segments;

internal sealed class SegmentWorld : IAsyncDisposable
{
    public static readonly string MappingDigest = "sha-256:" + new string('a', 64);
    public static readonly string PolicyDigest = "sha-256:" + new string('b', 64);

    private readonly IsolatedDatabase db;
    private readonly ServiceProvider provider;

    private SegmentWorld(IsolatedDatabase db, ServiceProvider provider, IObjectStorage storage)
    {
        this.db = db;
        this.provider = provider;
        Storage = storage;
    }

    public IObjectStorage Storage { get; }

    public Guid TeamId { get; private set; }

    public Guid MatchId { get; private set; }

    public Guid OtherTeamId { get; private set; }

    public Guid AdminId { get; private set; }

    public Guid CoachId { get; private set; }

    public Guid ViewerId { get; private set; }

    public Guid OtherTeamCoachId { get; private set; }

    public Guid RecordingVersionId { get; } = Guid.NewGuid();

    public static async Task<SegmentWorld> CreateAsync(PostgresContainerFixture postgres, RustFsContainerFixture rustfs, CancellationToken ct)
    {
        var db = await postgres.CreateDatabaseAsync(ct);
        (await MigratorHarness.RunAsync(db, TestMigrationCatalogs.Platform(), ct)).ExitCode
            .ShouldBe(MigratorExitCode.Success);
        var world = new SegmentWorld(db, PlatformServices.Build(db, time: new MutableTimeProvider(DateTimeOffset.UtcNow)), rustfs.CreateObjectStorage());
        await world.SeedAsync(ct);
        return world;
    }

    public Task ExecAsync(string sql, CancellationToken ct, bool superuser = false) => ExecuteAsync(sql, ct, superuser);

    public async Task<long> ScalarAsync(string sql, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(db.AppConnectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        return (long)(await command.ExecuteScalarAsync(ct))!;
    }

    public MaterializedSegmentIdentity Identity(int segmentNumber) =>
        new(RecordingVersionId, Sha256Digest.Parse(MappingDigest), Sha256Digest.Parse(PolicyDigest), segmentNumber);

    public async Task<T> InScopeAsync<T>(Guid? actor, Func<IServiceProvider, Task<T>> action)
    {
        await using var scope = provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<TestRequestContext>();
        context.ActorKind = AuditActorKind.Member;
        context.MemberAccountId = actor;
        return await action(scope.ServiceProvider);
    }

    public Task<T> InUnitOfWorkAsync<T>(Func<ISegmentStore, Task<T>> action, CancellationToken ct) =>
        InScopeAsync(null, async sp =>
        {
            await using var tx = await sp.GetRequiredService<IUnitOfWork>().BeginAsync(ct);
            var result = await action(sp.GetRequiredService<ISegmentStore>());
            await tx.CommitAsync(ct);
            return result;
        });

    public Task<OperationResult<SegmentPurgeSummary>> PurgeAsync(Guid actor, IObjectStorage? storage, CancellationToken ct) =>
        InScopeAsync(actor, sp => new PurgeMaterializedSegmentsHandler(
            sp.GetRequiredService<IUnitOfWork>(), sp.GetRequiredService<SocAlytics.Platform.Application.IdentityAccess.IAccessAuthorizer>(),
            Lineage(), sp.GetRequiredService<ISegmentStore>(), storage ?? Storage, sp.GetRequiredService<IAuditTrail>())
            .HandleAsync(MatchId, ct));

    public Task<OperationResult<MaterializedSegmentListing>> ListAsync(Guid actor, CancellationToken ct) =>
        InScopeAsync(actor, sp => new ListMaterializedSegmentsHandler(
            sp.GetRequiredService<IUnitOfWork>(), sp.GetRequiredService<SocAlytics.Platform.Application.IdentityAccess.IAccessAuthorizer>(),
            Lineage(), sp.GetRequiredService<ISegmentStore>())
            .HandleAsync(MatchId, ct));

    // Publishes an available segment whose objects exist in the store.
    public async Task<MaterializedSegmentRecord> PublishAsync(int segmentNumber, CancellationToken ct)
    {
        var identity = Identity(segmentNumber);
        await InUnitOfWorkAsync(store => store.DemandAsync(identity, 1, 30, ct), ct);
        var claimed = (await InUnitOfWorkAsync(store => store.ClaimDueAsync("w", TimeSpan.FromMinutes(5), 10, ct), ct))
            .Single(c => c.Segment.Identity == identity);
        var prefix = $"segments/purge-tests/{Guid.NewGuid():N}/";
        var media = await PutAsync(prefix + "segment.mp4", [1, 2, 3, (byte)segmentNumber], ct);
        var manifest = await PutAsync(prefix + "segment-manifest.json", [4, 5, 6, (byte)segmentNumber], ct);
        var association = new SegmentAssociation(prefix + "segment.mp4", prefix + "segment-manifest.json", media.ToString(), 4, manifest.ToString());
        (await InUnitOfWorkAsync(
            store => store.TryPublishAsync(claimed.Segment.Id, claimed.Segment.CandidateId!.Value, association, ct), ct)).ShouldBeTrue();
        return (await InUnitOfWorkAsync(store => store.FindAsync(identity, ct), ct))!;
    }

    public async Task AddJobAsync(int segmentNumber, string runState, string jobState, CancellationToken ct)
    {
        var run = Guid.NewGuid();
        await ExecAsync(
            $"""
            SET session_replication_role = replica;
            INSERT INTO socalytics.analysis_runs
                (id, team_id, match_id, recording_set_version_id, recording_set_finalized_at, workflow_id, workflow_version, requested_capabilities,
                 completion_policy, state, created_at, terminated_at, updated_at)
            VALUES ('{run}', '{TeamId}', '{MatchId}', gen_random_uuid(), now(), 'w', '1', ARRAY['c'],
                    'all-requested-capabilities', '{runState}', now(),
                    CASE WHEN '{runState}' IN ('Requested', 'Running') THEN NULL ELSE now() END, now());
            INSERT INTO socalytics.analysis_logical_jobs
                (id, run_id, node_id, execution_scope, recording_version_id, timeline_mapping_digest,
                 segmentation_policy_digest, segment_number, state, updated_at)
            VALUES (gen_random_uuid(), '{run}', gen_random_uuid(), 'segment', '{RecordingVersionId}',
                    '{MappingDigest}', '{PolicyDigest}', {segmentNumber}, '{jobState}', now());
            """,
            ct, superuser: true);
    }

    public async ValueTask DisposeAsync()
    {
        await provider.DisposeAsync();
        await db.DisposeAsync();
    }

    private async Task<Sha256Digest> PutAsync(string key, byte[] bytes, CancellationToken ct)
    {
        var digest = Sha256Digest.FromBytes(SHA256.HashData(bytes));
        await using var content = new MemoryStream(bytes);
        await Storage.PutObjectAsync(key, "application/octet-stream", content, bytes.Length, digest, ct);
        return digest;
    }

    private IRecordingLineageQueries Lineage()
    {
        var set = new RecordingSetVersionView(
            Guid.NewGuid(), MatchId, TeamId, DateTimeOffset.UnixEpoch,
            [new RecordingSetMemberView(1, RecordingVersionId, "sha-256-parts:x", Guid.NewGuid(), MappingDigest)]);
        return new FixedLineage(new MatchRecordingLineageView(MatchId, TeamId, [], [], [set]));
    }

    private async Task SeedAsync(CancellationToken ct)
    {
        AdminId = await TestMembers.SeedAsync(db, "admin", clubRoles: ["club-admin"], cancellationToken: ct);
        CoachId = await TestMembers.SeedAsync(db, "coach", cancellationToken: ct);
        ViewerId = await TestMembers.SeedAsync(db, "viewer", cancellationToken: ct);
        OtherTeamCoachId = await TestMembers.SeedAsync(db, "other-coach", cancellationToken: ct);
        var season = Guid.NewGuid();
        TeamId = Guid.NewGuid();
        OtherTeamId = Guid.NewGuid();
        MatchId = Guid.NewGuid();
        await ExecAsync(
            $"""
            INSERT INTO socalytics.season (id, name, created_at) VALUES ('{season}', 'S', now());
            INSERT INTO socalytics.team (id, season_id, name, created_at) VALUES ('{TeamId}', '{season}', 'T', now());
            INSERT INTO socalytics.team (id, season_id, name, created_at) VALUES ('{OtherTeamId}', '{season}', 'U', now());
            INSERT INTO socalytics.match (id, team_id, opponent_name, kickoff_at, home_away, created_at, created_by_account_id)
                VALUES ('{MatchId}', '{TeamId}', 'O', now(), 'home', now(), '{AdminId}');
            INSERT INTO socalytics.team_role_assignment (member_account_id, team_id, role, assigned_at, assigned_by_account_id)
                VALUES ('{CoachId}', '{TeamId}', 'coach', now(), '{AdminId}'),
                       ('{ViewerId}', '{TeamId}', 'viewer', now(), '{AdminId}'),
                       ('{OtherTeamCoachId}', '{OtherTeamId}', 'coach', now(), '{AdminId}');
            """,
            ct);
        // The segments use a synthetic recording version that the lineage fake describes.
        await ExecAsync(
            "ALTER TABLE socalytics.materialized_segments DROP CONSTRAINT materialized_segments_recording_version_id_fkey", ct, superuser: true);
    }

    private async Task ExecuteAsync(string sql, CancellationToken ct, bool superuser)
    {
        var connectionString = superuser
            ? new NpgsqlConnectionStringBuilder(db.SuperuserConnectionString) { Database = db.Name }.ConnectionString
            : db.AppConnectionString;
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(ct);
    }

    private sealed class FixedLineage(MatchRecordingLineageView view) : IRecordingLineageQueries
    {
        public Task<MatchRecordingLineageView> GetMatchLineageAsync(Guid matchId, Guid teamId, CancellationToken cancellationToken) =>
            Task.FromResult(view);

        public Task<RecordingSetVersionView?> GetSetVersionAsync(Guid matchId, Guid recordingSetVersionId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<RecordingVersion?> GetRecordingVersionAsync(Guid matchId, Guid recordingVersionId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<StoredTimelineMapping?> GetTimelineMappingAsync(Guid matchId, Guid timelineMappingId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}

// US4, FR-011, FR-024 to FR-027, SC-007
public sealed class SegmentPurgeTests(PostgresContainerFixture postgres, RustFsContainerFixture rustfs)
{
    private sealed class UnavailableStorage(IObjectStorage inner, int allowedDeletes) : DelegatingStorage(inner)
    {
        private int deletes;

        public override Task DeleteObjectAsync(string objectKey, CancellationToken cancellationToken) =>
            ++deletes > allowedDeletes ? throw new ObjectStorageUnavailableException() : base.DeleteObjectAsync(objectKey, cancellationToken);
    }

    [Fact]
    public async Task PurgeSkipsInUseRowsDeletesObjectsAndIsIdempotent()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var w = await SegmentWorld.CreateAsync(postgres, rustfs, ct);
        var free = await w.PublishAsync(1, ct);
        var pinned = await w.PublishAsync(2, ct);
        await w.AddJobAsync(2, "Running", "Waiting", ct);
        var finished = await w.PublishAsync(3, ct);
        await w.AddJobAsync(3, "Completed", "Ready", ct);
        var materializing = await w.InUnitOfWorkAsync(s => s.DemandAsync(w.Identity(4), 1, 30, ct), ct);
        await w.InUnitOfWorkAsync(s => s.ClaimDueAsync("w", TimeSpan.FromMinutes(5), 10, ct), ct);

        var listed = (await w.ListAsync(w.AdminId, ct)).Value;
        listed.Segments.Select(s => (s.SegmentNumber, s.InUse)).ShouldBe([(1, false), (2, true), (3, false), (4, true)]);
        listed.Segments[0].MediaSizeBytes.ShouldBe(4);

        var first = (await w.PurgeAsync(w.AdminId, null, ct)).Value;
        first.Purged.ShouldBe(2);
        first.InUse.ShouldBe(2);

        (await w.Storage.GetObjectChecksumAsync(free.Association!.MediaObjectKey, ct)).ShouldBeNull();
        (await w.Storage.GetObjectChecksumAsync(finished.Association!.ManifestObjectKey, ct)).ShouldBeNull();
        (await w.Storage.GetObjectChecksumAsync(pinned.Association!.MediaObjectKey, ct)).ShouldNotBeNull();
        var purged = (await w.InUnitOfWorkAsync(s => s.FindAsync(w.Identity(1), ct), ct))!;
        purged.State.ShouldBe(MaterializedSegmentState.Absent);
        purged.Demanded.ShouldBeFalse();
        purged.Association.ShouldBeNull();
        purged.Id.ShouldBe(free.Id);
        (await w.InUnitOfWorkAsync(s => s.FindAsync(w.Identity(2), ct), ct))!.State.ShouldBe(MaterializedSegmentState.Available);
        (await w.InUnitOfWorkAsync(s => s.FindAsync(materializing.Id, ct), ct))!.State.ShouldBe(MaterializedSegmentState.Materializing);

        var second = (await w.PurgeAsync(w.AdminId, null, ct)).Value;
        second.Purged.ShouldBe(0);
        second.InUse.ShouldBe(2);
        (await w.ScalarAsync("SELECT count(*) FROM socalytics.security_audit_event WHERE event_type = 'segments.purged'", ct)).ShouldBe(2);
    }

    [Fact]
    public async Task PurgedSegmentIsRematerializedForALaterRunWithTheSameValidation()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var w = await SegmentWorld.CreateAsync(postgres, rustfs, ct);
        var original = await w.PublishAsync(1, ct);
        (await w.PurgeAsync(w.AdminId, null, ct)).Value.Purged.ShouldBe(1);

        var demanded = await w.InUnitOfWorkAsync(s => s.DemandAsync(w.Identity(1), 1, 30, ct), ct);
        demanded.Id.ShouldBe(original.Id);
        demanded.State.ShouldBe(MaterializedSegmentState.Absent);
        demanded.Demanded.ShouldBeTrue();

        var claimed = (await w.InUnitOfWorkAsync(s => s.ClaimDueAsync("w2", TimeSpan.FromMinutes(5), 10, ct), ct)).Single();
        claimed.Segment.Id.ShouldBe(original.Id);
        claimed.Segment.State.ShouldBe(MaterializedSegmentState.Materializing);
    }

    [Fact]
    public async Task StorageUnavailabilityKeepsUnpurgedRowsAndAlreadyPurgedRowsStayPurged()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var w = await SegmentWorld.CreateAsync(postgres, rustfs, ct);
        await w.PublishAsync(1, ct);
        await w.PublishAsync(2, ct);

        // One row's two objects delete, then the store fails.
        var result = await w.PurgeAsync(w.AdminId, new UnavailableStorage(w.Storage, 2), ct);

        result.IsSuccess.ShouldBeFalse();
        result.Failure.Kind.ShouldBe(OperationFailureKind.Unavailable);
        (await w.ScalarAsync("SELECT count(*) FROM socalytics.materialized_segments WHERE state = 'absent'", ct)).ShouldBe(1);
        (await w.ScalarAsync("SELECT count(*) FROM socalytics.materialized_segments WHERE state = 'available'", ct)).ShouldBe(1);
    }
}

internal class DelegatingStorage(IObjectStorage inner) : IObjectStorage
{
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

    public Task PutObjectAsync(string objectKey, string contentType, Stream content, long contentLength, Sha256Digest digest, CancellationToken cancellationToken) =>
        inner.PutObjectAsync(objectKey, contentType, content, contentLength, digest, cancellationToken);

    public ObjectReadGrant PresignGetObject(string objectKey, DateTimeOffset expiresAt) =>
        inner.PresignGetObject(objectKey, expiresAt);

    public Task<ObjectChecksum?> GetObjectChecksumAsync(string objectKey, CancellationToken cancellationToken) =>
        inner.GetObjectChecksumAsync(objectKey, cancellationToken);

    public Task<ObjectReadResult?> ReadObjectAsync(string objectKey, long maxBytes, CancellationToken cancellationToken) =>
        inner.ReadObjectAsync(objectKey, maxBytes, cancellationToken);

    public Task AbortMultipartUploadAsync(MultipartUploadReference upload, CancellationToken cancellationToken) =>
        inner.AbortMultipartUploadAsync(upload, cancellationToken);

    public virtual Task DeleteObjectAsync(string objectKey, CancellationToken cancellationToken) =>
        inner.DeleteObjectAsync(objectKey, cancellationToken);
}
