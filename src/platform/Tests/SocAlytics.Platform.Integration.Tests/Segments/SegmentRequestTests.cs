using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.Recordings;
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

public sealed class SegmentRequestTests(PostgresContainerFixture postgres)
{
    private static readonly string MappingDigest = "sha-256:" + new string('a', 64);
    private static readonly string PolicyDigest = "sha-256:" + new string('b', 64);

    private sealed class FakeLineage(RecordingSetVersionView? set, StoredTimelineMapping? mapping) : IRecordingLineageQueries
    {
        public Task<MatchRecordingLineageView> GetMatchLineageAsync(Guid matchId, Guid teamId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<RecordingSetVersionView?> GetSetVersionAsync(Guid matchId, Guid recordingSetVersionId, CancellationToken cancellationToken) =>
            Task.FromResult(set is not null && set.MatchId == matchId && set.Id == recordingSetVersionId ? set : null);

        public Task<RecordingVersion?> GetRecordingVersionAsync(Guid matchId, Guid recordingVersionId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<StoredTimelineMapping?> GetTimelineMappingAsync(Guid matchId, Guid timelineMappingId, CancellationToken cancellationToken) =>
            Task.FromResult(mapping is not null && mapping.MatchId == matchId && mapping.Id == timelineMappingId ? mapping : null);
    }

    private sealed class Scenario
    {
        public Guid MatchId { get; } = Guid.NewGuid();
        public Guid TeamId { get; } = Guid.NewGuid();
        public Guid SetId { get; } = Guid.NewGuid();
        public Guid RecordingVersionId { get; } = Guid.NewGuid();
        public Guid MappingId { get; } = Guid.NewGuid();

        // One span: media [0, 60 s) maps to match time [0, 60 s).
        public FakeLineage Lineage()
        {
            var set = new RecordingSetVersionView(
                SetId, MatchId, TeamId, DateTimeOffset.UnixEpoch,
                [new RecordingSetMemberView(1, RecordingVersionId, "sha-256-parts:x", MappingId, MappingDigest)]);
            var mapping = new StoredTimelineMapping(
                MappingId, RecordingVersionId, MatchId, [new TimelineSpan(0, 60_000, 0)], MappingDigest, DateTimeOffset.UnixEpoch);
            return new FakeLineage(set, mapping);
        }

        public RequestSegmentMaterializationCommand Command(
            int segmentNumber = 1,
            int policyVersion = 1,
            int durationSeconds = 30,
            string? mappingDigest = null,
            string? policyDigest = null,
            Guid? teamId = null,
            Guid? matchId = null,
            Guid? recordingVersionId = null) =>
            new(matchId ?? MatchId, teamId ?? TeamId, SetId, recordingVersionId ?? RecordingVersionId,
                mappingDigest ?? MappingDigest, policyDigest ?? PolicyDigest, segmentNumber, policyVersion, durationSeconds);
    }

    private sealed class Harness(IsolatedDatabase db, ServiceProvider provider, MutableTimeProvider time) : IAsyncDisposable
    {
        public async Task<T> InUnitOfWorkAsync<T>(Func<ISegmentStore, Task<T>> action, CancellationToken ct)
        {
            await using var scope = provider.CreateAsyncScope();
            await using var uow = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().BeginAsync(ct);
            var store = new SegmentStore(scope.ServiceProvider.GetRequiredService<IDbSession>(), time);
            var result = await action(store);
            await uow.CommitAsync(ct);
            return result;
        }

        public Task<SegmentMaterializationRequestResult> RequestAsync(
            FakeLineage lineage, RequestSegmentMaterializationCommand command, CancellationToken ct) =>
            InUnitOfWorkAsync(store => new RequestSegmentMaterializationHandler(store, lineage).HandleAsync(command, ct), ct);

        public async Task<long> CountAsync(CancellationToken ct) => await ScalarAsync("SELECT count(*) FROM socalytics.materialized_segments", ct);

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
        var time = new MutableTimeProvider();
        var harness = new Harness(db, PlatformServices.Build(db, time: time), time);
        // The request tests use synthetic recording versions that the lineage fake describes.
        await harness.ExecSuperuserAsync(
            "ALTER TABLE socalytics.materialized_segments DROP CONSTRAINT materialized_segments_recording_version_id_fkey", ct);
        return harness;
    }

    [Fact]
    public async Task AcceptedRequestUpsertsDemandAndIsIdempotent()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await CreateAsync(ct);
        var s = new Scenario();

        var first = await h.RequestAsync(s.Lineage(), s.Command(), ct);
        var second = await h.RequestAsync(s.Lineage(), s.Command(), ct);

        first.IsAccepted.ShouldBeTrue();
        first.Segment!.State.ShouldBe(MaterializedSegmentState.Absent);
        first.Segment.Demanded.ShouldBeTrue();
        first.Segment.PolicyVersion.ShouldBe(1);
        first.Segment.DurationSeconds.ShouldBe(30);
        second.Segment!.Id.ShouldBe(first.Segment.Id);
        (await h.CountAsync(ct)).ShouldBe(1);
    }

    [Fact]
    public async Task DistinctIdentitiesCoexist()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await CreateAsync(ct);
        var s = new Scenario();

        var one = await h.RequestAsync(s.Lineage(), s.Command(segmentNumber: 1), ct);
        var two = await h.RequestAsync(s.Lineage(), s.Command(segmentNumber: 2), ct);
        var otherPolicy = await h.RequestAsync(s.Lineage(), s.Command(policyDigest: "sha-256:" + new string('c', 64)), ct);

        new[] { one, two, otherPolicy }.ShouldAllBe(r => r.IsAccepted);
        (await h.CountAsync(ct)).ShouldBe(3);
    }

    [Fact]
    public async Task UnknownPolicyVersionIsRejectedWithoutRow()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await CreateAsync(ct);
        var s = new Scenario();

        var result = await h.RequestAsync(s.Lineage(), s.Command(policyVersion: 2), ct);

        result.RejectionReason.ShouldBe(SegmentFailureReasons.PolicyUnknown);
        (await h.CountAsync(ct)).ShouldBe(0);
    }

    [Fact]
    public async Task ConflictingPolicyBindingIsRejectedAndChangesNoRow()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await CreateAsync(ct);
        var s = new Scenario();
        var bound = await h.RequestAsync(s.Lineage(), s.Command(durationSeconds: 30), ct);

        var result = await h.RequestAsync(s.Lineage(), s.Command(durationSeconds: 20), ct);

        result.RejectionReason.ShouldBe(SegmentFailureReasons.PolicyConflict);
        (await h.CountAsync(ct)).ShouldBe(1);
        var stored = await h.InUnitOfWorkAsync(store => store.FindAsync(bound.Segment!.Id, ct), ct);
        stored!.DurationSeconds.ShouldBe(30);
        stored.Version.ShouldBe(bound.Segment!.Version);
    }

    [Fact]
    public async Task ScopeDisagreementIsRejectedWithoutRow()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await CreateAsync(ct);
        var s = new Scenario();
        var otherMapping = "sha-256:" + new string('e', 64);

        var results = new[]
        {
            await h.RequestAsync(s.Lineage(), s.Command(teamId: Guid.NewGuid()), ct),
            await h.RequestAsync(s.Lineage(), s.Command(matchId: Guid.NewGuid()), ct),
            await h.RequestAsync(s.Lineage(), s.Command(recordingVersionId: Guid.NewGuid()), ct),
            await h.RequestAsync(s.Lineage(), s.Command(mappingDigest: otherMapping), ct),
        };

        results.ShouldAllBe(r => r.RejectionReason == SegmentFailureReasons.ScopeMismatch);
        (await h.CountAsync(ct)).ShouldBe(0);
    }

    [Fact]
    public async Task EmptyCoverageIntersectionIsRejectedWithoutRow()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await CreateAsync(ct);
        var s = new Scenario();

        // The mapping covers 60 s; segment 3 with 30 s windows owns [60 s, 90 s).
        var result = await h.RequestAsync(s.Lineage(), s.Command(segmentNumber: 3), ct);

        result.RejectionReason.ShouldBe(SegmentFailureReasons.CoverageEmpty);
        (await h.CountAsync(ct)).ShouldBe(0);
    }

    [Fact]
    public async Task MissingOrNonpositiveFieldsAreRejectedWithoutRow()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await CreateAsync(ct);
        var s = new Scenario();

        var results = new[]
        {
            await h.RequestAsync(s.Lineage(), s.Command(segmentNumber: 0), ct),
            await h.RequestAsync(s.Lineage(), s.Command(durationSeconds: 0), ct),
            await h.RequestAsync(s.Lineage(), s.Command(mappingDigest: "not-a-digest"), ct),
            await h.RequestAsync(s.Lineage(), s.Command(policyDigest: ""), ct),
            await h.RequestAsync(s.Lineage(), s.Command(recordingVersionId: Guid.Empty), ct),
        };

        results.ShouldAllBe(r => r.RejectionReason == RequestSegmentMaterializationHandler.IdentityInvalid);
        (await h.CountAsync(ct)).ShouldBe(0);
    }

    [Fact]
    public async Task RejectionLeavesAvailableAndFailedRowsUnchanged()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await CreateAsync(ct);
        var s = new Scenario();
        var accepted = await h.RequestAsync(s.Lineage(), s.Command(), ct);
        var claimed = (await h.InUnitOfWorkAsync(store => store.ClaimDueAsync("w", TimeSpan.FromMinutes(1), 1, ct), ct)).Single();
        var association = new SegmentAssociation(
            "segments/k/segment.mp4", "segments/k/segment-manifest.json", "sha-256:" + new string('c', 64), 10,
            "sha-256:" + new string('d', 64));
        (await h.InUnitOfWorkAsync(
            store => store.TryPublishAsync(accepted.Segment!.Id, claimed.Segment.CandidateId!.Value, association, ct), ct))
            .ShouldBeTrue();
        var before = await h.InUnitOfWorkAsync(store => store.FindAsync(accepted.Segment!.Id, ct), ct);

        var rejected = await h.RequestAsync(s.Lineage(), s.Command(durationSeconds: 10), ct);
        var after = await h.InUnitOfWorkAsync(store => store.FindAsync(accepted.Segment!.Id, ct), ct);

        rejected.RejectionReason.ShouldBe(SegmentFailureReasons.PolicyConflict);
        after.ShouldBe(before);
        after!.State.ShouldBe(MaterializedSegmentState.Available);
    }
}
