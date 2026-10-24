using Shouldly;
using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Integration.Tests.Recordings;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Segments;

// FR-024 to FR-027: only Club Admins list and purge; refusals disclose no segments and purge nothing.
public sealed class SegmentAccessTests(PostgresContainerFixture postgres, RustFsContainerFixture rustfs)
{
    [Fact]
    public async Task NonAdminsAreRefusedWithoutDisclosureAndNothingIsPurged()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var w = await SegmentWorld.CreateAsync(postgres, rustfs, ct);
        var segment = await w.PublishAsync(1, ct);

        foreach (var member in new[] { w.CoachId, w.ViewerId })
        {
            var list = await w.ListAsync(member, ct);
            list.IsSuccess.ShouldBeFalse();
            list.Failure.Kind.ShouldBe(OperationFailureKind.Forbidden);

            var purge = await w.PurgeAsync(member, null, ct);
            purge.IsSuccess.ShouldBeFalse();
            purge.Failure.Kind.ShouldBe(OperationFailureKind.Forbidden);
        }

        // A member of another team cannot see the match at all.
        (await w.ListAsync(w.OtherTeamCoachId, ct)).Failure.Kind.ShouldBe(OperationFailureKind.NotFound);
        (await w.PurgeAsync(w.OtherTeamCoachId, null, ct)).Failure.Kind.ShouldBe(OperationFailureKind.NotFound);

        (await w.Storage.GetObjectChecksumAsync(segment.Association!.MediaObjectKey, ct)).ShouldNotBeNull();
        (await w.ScalarAsync("SELECT count(*) FROM socalytics.materialized_segments WHERE state = 'available'", ct)).ShouldBe(1);
        (await w.ScalarAsync("SELECT count(*) FROM socalytics.security_audit_event WHERE event_type = 'segments.purged'", ct)).ShouldBe(0);
    }

    [Fact]
    public async Task ClubAdminListsWithoutObjectKeysOrMediaDigest()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var w = await SegmentWorld.CreateAsync(postgres, rustfs, ct);
        await w.PublishAsync(1, ct);

        var listing = (await w.ListAsync(w.AdminId, ct)).Value;

        listing.MatchId.ShouldBe(w.MatchId);
        listing.Segments.Count.ShouldBe(1);
        typeof(SocAlytics.Platform.Application.Segments.MaterializedSegmentView).GetProperties()
            .Select(p => p.Name).ShouldNotContain(n => n.Contains("Key") || n.Contains("Digest") && n.StartsWith("Media"));
    }
}
