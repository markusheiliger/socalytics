using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.Segments;
using SocAlytics.Platform.Domain.Recordings;
using SocAlytics.Platform.Domain.Segments;
using SocAlytics.Platform.Infrastructure.Persistence;
using SocAlytics.Platform.Infrastructure.Persistence.Segments;
using SocAlytics.Platform.Integration.Tests.IdentityAccess.Support;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Migrator;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Segments;

public sealed class SegmentStoreTests(PostgresContainerFixture postgres)
{
    private static readonly string MappingDigest = "sha-256:" + new string('a', 64);
    private static readonly string PolicyDigest = "sha-256:" + new string('b', 64);
    private static readonly SegmentAssociation Association = new(
        "segments/k/segment.mp4", "segments/k/segment-manifest.json", "sha-256:" + new string('c', 64), 1234,
        "sha-256:" + new string('d', 64));

    private sealed class Harness(IsolatedDatabase db, ServiceProvider provider, MutableTimeProvider time) : IAsyncDisposable
    {
        public IsolatedDatabase Db { get; } = db;

        public MutableTimeProvider Time { get; } = time;

        public async Task<T> InUnitOfWorkAsync<T>(Func<ISegmentStore, Task<T>> action, CancellationToken ct)
        {
            await using var scope = provider.CreateAsyncScope();
            await using var uow = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().BeginAsync(ct);
            var store = new SegmentStore(scope.ServiceProvider.GetRequiredService<IDbSession>(), Time);
            var result = await action(store);
            await uow.CommitAsync(ct);
            return result;
        }

        public async Task ExecAsync(string sql, CancellationToken ct, bool superuser = false)
        {
            var connectionString = superuser
                ? new NpgsqlConnectionStringBuilder(Db.SuperuserConnectionString) { Database = Db.Name }.ConnectionString
                : Db.AppConnectionString;
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(ct);
            await using var command = new NpgsqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync(ct);
        }

        public async Task<T?> ScalarAsync<T>(string sql, CancellationToken ct)
        {
            await using var connection = new NpgsqlConnection(Db.AppConnectionString);
            await connection.OpenAsync(ct);
            await using var command = new NpgsqlCommand(sql, connection);
            var value = await command.ExecuteScalarAsync(ct);
            return value is null or DBNull ? default : (T)value;
        }

        public async ValueTask DisposeAsync()
        {
            await provider.DisposeAsync();
            await Db.DisposeAsync();
        }
    }

    private async Task<Harness> CreateAsync(CancellationToken ct)
    {
        var db = await postgres.CreateDatabaseAsync(ct);
        (await MigratorHarness.RunAsync(db, TestMigrationCatalogs.Platform(), ct)).ExitCode
            .ShouldBe(MigratorExitCode.Success);
        var time = new MutableTimeProvider();
        var harness = new Harness(db, PlatformServices.Build(db, time: time), time);
        // The store tests do not need real recording versions.
        await harness.ExecAsync(
            "ALTER TABLE socalytics.materialized_segments DROP CONSTRAINT materialized_segments_recording_version_id_fkey",
            ct, superuser: true);
        return harness;
    }

    private static MaterializedSegmentIdentity Identity(Guid? recordingVersion = null, int number = 1) =>
        new(recordingVersion ?? Guid.NewGuid(), Sha256Digest.Parse(MappingDigest), Sha256Digest.Parse(PolicyDigest), number);

    [Fact]
    public async Task DemandCreatesAbsentDemandedRowAndIsIdempotent()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await CreateAsync(ct);
        var identity = Identity();

        var first = await h.InUnitOfWorkAsync(s => s.DemandAsync(identity, 1, 5, ct), ct);
        var second = await h.InUnitOfWorkAsync(s => s.DemandAsync(identity, 1, 5, ct), ct);

        first.State.ShouldBe(MaterializedSegmentState.Absent);
        first.Demanded.ShouldBeTrue();
        first.Identity.ShouldBe(identity);
        first.PolicyVersion.ShouldBe(1);
        first.DurationSeconds.ShouldBe(5);
        second.Id.ShouldBe(first.Id);
        (await h.ScalarAsync<long>("SELECT count(*) FROM socalytics.materialized_segments", ct)).ShouldBe(1);
    }

    [Fact]
    public async Task ClaimLeasesDueRowsAndSkipsRowsNotDue()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await CreateAsync(ct);
        var due = await h.InUnitOfWorkAsync(s => s.DemandAsync(Identity(number: 1), 1, 5, ct), ct);
        var later = await h.InUnitOfWorkAsync(s => s.DemandAsync(Identity(number: 2), 1, 5, ct), ct);
        var candidate = await h.InUnitOfWorkAsync(s => s.ClaimDueAsync("w1", TimeSpan.FromMinutes(1), 1, ct), ct);
        var claimed = candidate.Single();
        await h.InUnitOfWorkAsync(s => s.TryRecordTransientFailureAsync(
            claimed.Segment.Id, claimed.Segment.CandidateId!.Value, h.Time.GetUtcNow().AddMinutes(10), ct), ct);

        var claims = await h.InUnitOfWorkAsync(s => s.ClaimDueAsync("w1", TimeSpan.FromMinutes(1), 10, ct), ct);

        claimed.Segment.State.ShouldBe(MaterializedSegmentState.Materializing);
        claimed.Segment.AttemptsUsed.ShouldBe(1);
        claimed.ReclaimedCandidateId.ShouldBeNull();
        claims.Select(c => c.Segment.Id).ShouldBe([claimed.Segment.Id == due.Id ? later.Id : due.Id]);
        claims.Single().Segment.LeaseOwner.ShouldBe("w1");
        claims.Single().Segment.LeaseExpiresAt.ShouldBe(h.Time.GetUtcNow().AddMinutes(1));
    }

    [Fact]
    public async Task ClaimReclaimsExpiredLeaseWithNewCandidate()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await CreateAsync(ct);
        await h.InUnitOfWorkAsync(s => s.DemandAsync(Identity(), 1, 5, ct), ct);
        var first = (await h.InUnitOfWorkAsync(s => s.ClaimDueAsync("w1", TimeSpan.FromMinutes(1), 1, ct), ct)).Single();

        (await h.InUnitOfWorkAsync(s => s.ClaimDueAsync("w2", TimeSpan.FromMinutes(1), 1, ct), ct)).ShouldBeEmpty();
        h.Time.Advance(TimeSpan.FromMinutes(2));
        var second = (await h.InUnitOfWorkAsync(s => s.ClaimDueAsync("w2", TimeSpan.FromMinutes(1), 1, ct), ct)).Single();

        second.ReclaimedCandidateId.ShouldBe(first.Segment.CandidateId);
        second.Segment.CandidateId.ShouldNotBe(first.Segment.CandidateId);
        second.Segment.LeaseOwner.ShouldBe("w2");
        second.Segment.AttemptsUsed.ShouldBe(2);
    }

    [Fact]
    public async Task ConcurrentClaimsNeverShareRows()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await CreateAsync(ct);
        await h.InUnitOfWorkAsync(s => s.DemandAsync(Identity(number: 1), 1, 5, ct), ct);
        await h.InUnitOfWorkAsync(s => s.DemandAsync(Identity(number: 2), 1, 5, ct), ct);
        await h.InUnitOfWorkAsync(s => s.DemandAsync(Identity(number: 3), 1, 5, ct), ct);

        // Both claims run in open transactions, so the second must skip the rows the first holds.
        IReadOnlyList<ClaimedSegment> a = [];
        IReadOnlyList<ClaimedSegment> b = [];
        await h.InUnitOfWorkAsync(async s1 =>
        {
            a = await s1.ClaimDueAsync("w1", TimeSpan.FromMinutes(1), 2, ct);
            b = await h.InUnitOfWorkAsync(s2 => s2.ClaimDueAsync("w2", TimeSpan.FromMinutes(1), 2, ct), ct);
            return 0;
        }, ct);

        a.Count.ShouldBe(2);
        b.Count.ShouldBe(1);
        a.Select(c => c.Segment.Id).Intersect(b.Select(c => c.Segment.Id)).ShouldBeEmpty();
    }

    [Fact]
    public async Task PublicationIsCompareAndSetOnCandidate()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await CreateAsync(ct);
        var identity = Identity();
        await h.InUnitOfWorkAsync(s => s.DemandAsync(identity, 1, 5, ct), ct);
        var claim = (await h.InUnitOfWorkAsync(s => s.ClaimDueAsync("w1", TimeSpan.FromMinutes(1), 1, ct), ct)).Single();
        var id = claim.Segment.Id;
        var candidate = claim.Segment.CandidateId!.Value;

        (await h.InUnitOfWorkAsync(s => s.TryPublishAsync(id, Guid.NewGuid(), Association, ct), ct)).ShouldBeFalse();
        (await h.InUnitOfWorkAsync(s => s.TryPublishAsync(id, candidate, Association, ct), ct)).ShouldBeTrue();
        (await h.InUnitOfWorkAsync(s => s.TryPublishAsync(id, candidate, Association, ct), ct)).ShouldBeFalse();

        var row = (await h.InUnitOfWorkAsync(s => s.FindAsync(identity, ct), ct))!;
        row.State.ShouldBe(MaterializedSegmentState.Available);
        row.Association.ShouldBe(Association);
        row.CandidateId.ShouldBeNull();
        row.LeaseOwner.ShouldBeNull();
        row.AvailableAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task StaleCandidateCannotPublishFailOrRetryAfterReclaim()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await CreateAsync(ct);
        await h.InUnitOfWorkAsync(s => s.DemandAsync(Identity(), 1, 5, ct), ct);
        var stale = (await h.InUnitOfWorkAsync(s => s.ClaimDueAsync("w1", TimeSpan.FromMinutes(1), 1, ct), ct)).Single();
        h.Time.Advance(TimeSpan.FromMinutes(2));
        var current = (await h.InUnitOfWorkAsync(s => s.ClaimDueAsync("w2", TimeSpan.FromMinutes(1), 1, ct), ct)).Single();
        var id = stale.Segment.Id;
        var staleCandidate = stale.Segment.CandidateId!.Value;

        (await h.InUnitOfWorkAsync(s => s.TryPublishAsync(id, staleCandidate, Association, ct), ct)).ShouldBeFalse();
        (await h.InUnitOfWorkAsync(s => s.TryFailAsync(id, staleCandidate, SegmentFailureReasons.ExtractionFailed, ct), ct))
            .ShouldBeFalse();
        (await h.InUnitOfWorkAsync(s => s.TryRecordTransientFailureAsync(id, staleCandidate, h.Time.GetUtcNow(), ct), ct))
            .ShouldBeFalse();
        (await h.InUnitOfWorkAsync(s => s.FindAsync(id, ct), ct))!.CandidateId.ShouldBe(current.Segment.CandidateId);
    }

    [Fact]
    public async Task TransientFailureDelaysRetryAndFailureIsReDemandedWithResetBudget()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await CreateAsync(ct);
        var identity = Identity();
        await h.InUnitOfWorkAsync(s => s.DemandAsync(identity, 1, 5, ct), ct);
        var claim = (await h.InUnitOfWorkAsync(s => s.ClaimDueAsync("w1", TimeSpan.FromMinutes(1), 1, ct), ct)).Single();
        var retryAt = h.Time.GetUtcNow().AddMinutes(5);

        (await h.InUnitOfWorkAsync(s => s.TryRecordTransientFailureAsync(
            claim.Segment.Id, claim.Segment.CandidateId!.Value, retryAt, ct), ct)).ShouldBeTrue();
        var waiting = (await h.InUnitOfWorkAsync(s => s.FindAsync(identity, ct), ct))!;
        waiting.State.ShouldBe(MaterializedSegmentState.Absent);
        waiting.Demanded.ShouldBeTrue();
        waiting.NextAttemptAt.ShouldBe(retryAt);
        (await h.InUnitOfWorkAsync(s => s.ClaimDueAsync("w1", TimeSpan.FromMinutes(1), 1, ct), ct)).ShouldBeEmpty();

        h.Time.Advance(TimeSpan.FromMinutes(6));
        var again = (await h.InUnitOfWorkAsync(s => s.ClaimDueAsync("w1", TimeSpan.FromMinutes(1), 1, ct), ct)).Single();
        again.Segment.AttemptsUsed.ShouldBe(2);
        (await h.InUnitOfWorkAsync(s => s.TryFailAsync(
            again.Segment.Id, again.Segment.CandidateId!.Value, SegmentFailureReasons.RetryBudgetExhausted, ct), ct))
            .ShouldBeTrue();
        var failed = (await h.InUnitOfWorkAsync(s => s.FindAsync(identity, ct), ct))!;
        failed.State.ShouldBe(MaterializedSegmentState.Failed);
        failed.FailureReason.ShouldBe(SegmentFailureReasons.RetryBudgetExhausted);
        (await h.InUnitOfWorkAsync(s => s.ClaimDueAsync("w1", TimeSpan.FromMinutes(1), 1, ct), ct)).ShouldBeEmpty();

        var redemanded = await h.InUnitOfWorkAsync(s => s.DemandAsync(identity, 1, 5, ct), ct);
        redemanded.State.ShouldBe(MaterializedSegmentState.Absent);
        redemanded.AttemptsUsed.ShouldBe(0);
        redemanded.FailureReason.ShouldBeNull();
        (await h.InUnitOfWorkAsync(s => s.ClaimDueAsync("w1", TimeSpan.FromMinutes(1), 1, ct), ct)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task FailureRejectsUnknownReasons()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await CreateAsync(ct);
        await Should.ThrowAsync<ArgumentException>(() =>
            h.InUnitOfWorkAsync(s => s.TryFailAsync(Guid.NewGuid(), Guid.NewGuid(), "ffmpeg exited with 1 at /tmp/x", ct), ct));
    }

    [Fact]
    public async Task InvalidateAvailableReturnsAssociationAndFailsRow()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await CreateAsync(ct);
        var identity = Identity();
        await h.InUnitOfWorkAsync(s => s.DemandAsync(identity, 1, 5, ct), ct);
        var claim = (await h.InUnitOfWorkAsync(s => s.ClaimDueAsync("w1", TimeSpan.FromMinutes(1), 1, ct), ct)).Single();
        await h.InUnitOfWorkAsync(s => s.TryPublishAsync(claim.Segment.Id, claim.Segment.CandidateId!.Value, Association, ct), ct);

        (await h.InUnitOfWorkAsync(s => s.TryInvalidateAvailableAsync(
            claim.Segment.Id, "sha-256:" + new string('0', 64), SegmentFailureReasons.MediaInvalid, ct), ct)).ShouldBeNull();
        var old = await h.InUnitOfWorkAsync(s => s.TryInvalidateAvailableAsync(
            claim.Segment.Id, Association.MediaDigest, SegmentFailureReasons.MediaInvalid, ct), ct);

        old.ShouldBe(Association);
        var row = (await h.InUnitOfWorkAsync(s => s.FindAsync(identity, ct), ct))!;
        row.State.ShouldBe(MaterializedSegmentState.Failed);
        row.Association.ShouldBeNull();
        row.FailureReason.ShouldBe(SegmentFailureReasons.MediaInvalid);
    }

    [Fact]
    public async Task ListsByRecordingVersionsInSegmentOrder()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await CreateAsync(ct);
        var v1 = Guid.NewGuid();
        var v2 = Guid.NewGuid();
        await h.InUnitOfWorkAsync(s => s.DemandAsync(Identity(v1, 2), 1, 5, ct), ct);
        await h.InUnitOfWorkAsync(s => s.DemandAsync(Identity(v1, 1), 1, 5, ct), ct);
        await h.InUnitOfWorkAsync(s => s.DemandAsync(Identity(v2, 1), 1, 5, ct), ct);
        await h.InUnitOfWorkAsync(s => s.DemandAsync(Identity(Guid.NewGuid(), 1), 1, 5, ct), ct);

        var rows = await h.InUnitOfWorkAsync(s => s.ListByRecordingVersionsAsync([v1, v2], ct), ct);

        rows.Count.ShouldBe(3);
        rows.Where(r => r.Identity.RecordingVersionId == v1).Select(r => r.Identity.SegmentNumber).ShouldBe([1, 2]);
        (await h.InUnitOfWorkAsync(s => s.FindAsync(Identity(Guid.NewGuid()), ct), ct)).ShouldBeNull();
    }

    private static async Task InsertJobAsync(
        Harness h, Guid runId, string runState, MaterializedSegmentIdentity identity, string jobState, CancellationToken ct)
    {
        // Foreign keys to workflow nodes are bypassed because the store only reads job identity and state.
        var node = Guid.NewGuid();
        await h.ExecAsync(
            $"""
            SET session_replication_role = replica;
            INSERT INTO socalytics.analysis_runs
                (id, team_id, match_id, recording_set_version_id, recording_set_finalized_at, workflow_id, workflow_version, requested_capabilities,
                 completion_policy, state, created_at, terminated_at, updated_at)
            VALUES ('{runId}', gen_random_uuid(), gen_random_uuid(), gen_random_uuid(), now(), 'w', '1', ARRAY['c'],
                    'all-requested-capabilities', '{runState}', now(),
                    CASE WHEN '{runState}' IN ('Requested', 'Running') THEN NULL ELSE now() END, now())
            ON CONFLICT (id) DO NOTHING;
            INSERT INTO socalytics.analysis_logical_jobs
                (id, run_id, node_id, execution_scope, recording_version_id, timeline_mapping_digest,
                 segmentation_policy_digest, segment_number, state, updated_at)
            VALUES (gen_random_uuid(), '{runId}', '{node}', 'segment', '{identity.RecordingVersionId}',
                    '{identity.TimelineMappingDigest}', '{identity.SegmentationPolicyDigest}', {identity.SegmentNumber},
                    '{jobState}', now());
            """,
            ct, superuser: true);
    }

    [Fact]
    public async Task WaitingJobQueryJoinsLogicalJobsOfActiveRuns()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await CreateAsync(ct);
        var identity = Identity();
        var activeRun = Guid.NewGuid();
        await InsertJobAsync(h, activeRun, "Running", identity, "Waiting", ct);
        await InsertJobAsync(h, Guid.NewGuid(), "Running", identity, "Ready", ct);
        await InsertJobAsync(h, Guid.NewGuid(), "Failed", identity, "Waiting", ct);
        await InsertJobAsync(h, Guid.NewGuid(), "Running", Identity(identity.RecordingVersionId, 2), "Waiting", ct);

        var jobs = await h.InUnitOfWorkAsync(s => s.ListWaitingJobsAsync(identity, ct), ct);

        jobs.Select(j => j.RunId).ShouldBe([activeRun]);
    }

    [Fact]
    public async Task PurgeLockingMarksInUseRowsAndPurgesOthers()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await CreateAsync(ct);
        var version = Guid.NewGuid();
        var inUse = Identity(version, 1);
        var free = Identity(version, 2);
        var failed = Identity(version, 3);
        var materializing = Identity(version, 4);
        foreach (var identity in new[] { inUse, free, failed, materializing })
        {
            await h.InUnitOfWorkAsync(s => s.DemandAsync(identity, 1, 5, ct), ct);
        }

        var claims = await h.InUnitOfWorkAsync(s => s.ClaimDueAsync("w1", TimeSpan.FromMinutes(1), 4, ct), ct);
        foreach (var claim in claims.Where(c => c.Segment.Identity.SegmentNumber != 4))
        {
            var number = claim.Segment.Identity.SegmentNumber;
            await h.InUnitOfWorkAsync(s => number == 3
                ? s.TryFailAsync(claim.Segment.Id, claim.Segment.CandidateId!.Value, SegmentFailureReasons.ExtractionFailed, ct)
                : s.TryPublishAsync(claim.Segment.Id, claim.Segment.CandidateId!.Value, Association, ct), ct);
        }

        await InsertJobAsync(h, Guid.NewGuid(), "Running", inUse, "Claimed", ct);

        var purged = await h.InUnitOfWorkAsync(async s =>
        {
            var locked = await s.LockForPurgeAsync([version], ct);
            foreach (var candidate in locked.Where(c => !c.InUse))
            {
                (await s.TryPurgeAsync(candidate.Segment.Id, ct)).ShouldBeTrue();
            }

            return locked;
        }, ct);

        purged.Where(c => c.InUse).Select(c => c.Segment.Identity.SegmentNumber).Order().ShouldBe([1, 4]);
        purged.Count.ShouldBe(4);
        var free2 = (await h.InUnitOfWorkAsync(s => s.FindAsync(free, ct), ct))!;
        free2.State.ShouldBe(MaterializedSegmentState.Absent);
        free2.Demanded.ShouldBeFalse();
        free2.Association.ShouldBeNull();
        free2.PurgedAt.ShouldNotBeNull();
        (await h.InUnitOfWorkAsync(s => s.FindAsync(failed, ct), ct))!.FailureReason.ShouldBeNull();
        (await h.InUnitOfWorkAsync(s => s.FindAsync(inUse, ct), ct))!.State.ShouldBe(MaterializedSegmentState.Available);
        (await h.InUnitOfWorkAsync(s => s.ClaimDueAsync("w2", TimeSpan.FromMinutes(1), 10, ct), ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task WritesOutsideUnitOfWorkAreRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await CreateAsync(ct);
        await using var provider = PlatformServices.Build(h.Db);
        await using var scope = provider.CreateAsyncScope();
        var store = new SegmentStore(scope.ServiceProvider.GetRequiredService<IDbSession>(), TimeProvider.System);

        await Should.ThrowAsync<InvalidOperationException>(() => store.DemandAsync(Identity(), 1, 5, ct));
        await Should.ThrowAsync<InvalidOperationException>(() => store.ClaimDueAsync("w", TimeSpan.FromMinutes(1), 1, ct));
    }

    [Theory]
    [InlineData("UPDATE socalytics.materialized_segments SET state = 'available'")]
    [InlineData("UPDATE socalytics.materialized_segments SET state = 'materializing'")]
    [InlineData("UPDATE socalytics.materialized_segments SET state = 'bogus'")]
    [InlineData("UPDATE socalytics.materialized_segments SET media_object_key = 'k'")]
    [InlineData("UPDATE socalytics.materialized_segments SET candidate_id = gen_random_uuid()")]
    [InlineData("UPDATE socalytics.materialized_segments SET duration_seconds = 0")]
    [InlineData("UPDATE socalytics.materialized_segments SET segment_number = 0")]
    public async Task StateConstraintsRejectInconsistentRows(string update)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await CreateAsync(ct);
        await h.InUnitOfWorkAsync(s => s.DemandAsync(Identity(), 1, 5, ct), ct);

        var ex = await Should.ThrowAsync<PostgresException>(() => h.ExecAsync(update, ct));

        ex.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
    }

    [Fact]
    public async Task VersionTriggerAdvancesOnEveryChange()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await CreateAsync(ct);
        var identity = Identity();
        var created = await h.InUnitOfWorkAsync(s => s.DemandAsync(identity, 1, 5, ct), ct);
        var claim = (await h.InUnitOfWorkAsync(s => s.ClaimDueAsync("w1", TimeSpan.FromMinutes(1), 1, ct), ct)).Single();
        await h.InUnitOfWorkAsync(s => s.TryPublishAsync(claim.Segment.Id, claim.Segment.CandidateId!.Value, Association, ct), ct);
        var published = (await h.InUnitOfWorkAsync(s => s.FindAsync(identity, ct), ct))!;

        created.Version.ShouldBe(1);
        claim.Segment.Version.ShouldBe(2);
        published.Version.ShouldBe(3);
    }
}
