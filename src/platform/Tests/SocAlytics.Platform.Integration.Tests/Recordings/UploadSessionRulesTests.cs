using Shouldly;
using SocAlytics.Platform.Domain.Recordings;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Recordings;

public sealed class UploadSessionRulesTests
{
	private static readonly DateTimeOffset Created = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
	private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);
	private static readonly MultipartBounds Bounds = new(5L << 40, 5L << 20, 5L << 30, 10_000);

	private static UploadSession NewSession(Guid? id = null, Guid? matchId = null, string prefix = "")
	{
		var digest = Sha256Digest.FromBytes(new byte[32]).ToString();
		MultipartDeclaration.TryCreate(10, 10, [digest], Bounds, out var declaration, out _).ShouldBeTrue();
		RecordingDescriptor.TryCreate("Camera", null, "video/mp4", out var descriptor, out _).ShouldBeTrue();
		return UploadSession.Start(
			id ?? Guid.NewGuid(), matchId ?? Guid.NewGuid(), Guid.NewGuid(), descriptor!, declaration!,
			prefix, "upload-1", Guid.NewGuid(), Created, Lifetime);
	}

	[Fact]
	public void StartIsPendingWithExpiryFromLifetime()
	{
		var session = NewSession();
		session.State.ShouldBe(UploadSessionState.Pending);
		session.ExpiresAt.ShouldBe(Created + Lifetime);
		session.CompletedAt.ShouldBeNull();
		session.ExpiredAt.ShouldBeNull();
		session.StorageReleasedAt.ShouldBeNull();
	}

	[Fact]
	public void ObjectKeyLayout()
	{
		var id = Guid.NewGuid();
		var match = Guid.NewGuid();
		NewSession(id, match, "rec/").ObjectKey.ShouldBe($"rec/matches/{match}/upload-sessions/{id}");
		NewSession(id, match).ObjectKey.ShouldBe($"matches/{match}/upload-sessions/{id}");
	}

	[Fact]
	public void StoredStatesRoundTrip()
	{
		UploadSessionState.Pending.ToStored().ShouldBe("pending");
		UploadSessionState.Completed.ToStored().ShouldBe("completed");
		UploadSessionState.Expired.ToStored().ShouldBe("expired");
		foreach (var state in Enum.GetValues<UploadSessionState>())
		{
			UploadSessionStates.FromStored(state.ToStored()).ShouldBe(state);
		}
	}

	[Fact]
	public void ExpiryBoundaryIsDueAtExactlyExpiresAt()
	{
		var session = NewSession();
		var before = session.ExpiresAt.AddTicks(-1);
		session.CanIssueGrants(before).ShouldBeTrue();
		session.CanComplete(before).ShouldBeTrue();
		session.IsDueForExpiry(before).ShouldBeFalse();

		session.CanIssueGrants(session.ExpiresAt).ShouldBeFalse();
		session.CanComplete(session.ExpiresAt).ShouldBeFalse();
		session.IsDueForExpiry(session.ExpiresAt).ShouldBeTrue();
		session.IsReportedExpired(session.ExpiresAt).ShouldBeTrue();
	}

	[Fact]
	public void PendingCompletesOnce()
	{
		var session = NewSession();
		var now = Created.AddMinutes(1);
		session.TryComplete(now).ShouldBeTrue();
		session.State.ShouldBe(UploadSessionState.Completed);
		session.CompletedAt.ShouldBe(now);
		session.TryComplete(now).ShouldBeFalse();
		session.TryExpire(session.ExpiresAt).ShouldBeFalse();
		session.CanIssueGrants(now).ShouldBeFalse();
		session.IsDueForExpiry(session.ExpiresAt).ShouldBeFalse();
		session.TryMarkStorageReleased(now).ShouldBeFalse();
	}

	[Fact]
	public void CompletionRefusedWhenExpiredEvenBeforeWorkerRuns()
	{
		var session = NewSession();
		session.TryComplete(session.ExpiresAt).ShouldBeFalse();
		session.State.ShouldBe(UploadSessionState.Pending);
	}

	[Fact]
	public void ExpiryOnlyWhenDueThenStorageReleasedOnce()
	{
		var session = NewSession();
		session.TryExpire(session.ExpiresAt.AddTicks(-1)).ShouldBeFalse();
		session.TryMarkStorageReleased(Created).ShouldBeFalse();

		session.TryExpire(session.ExpiresAt).ShouldBeTrue();
		session.State.ShouldBe(UploadSessionState.Expired);
		session.ExpiredAt.ShouldBe(session.ExpiresAt);
		session.TryExpire(session.ExpiresAt).ShouldBeFalse();
		session.TryComplete(session.ExpiresAt).ShouldBeFalse();
		session.CanIssueGrants(session.ExpiresAt).ShouldBeFalse();
		session.IsReportedExpired(Created).ShouldBeTrue();

		var released = session.ExpiresAt.AddMinutes(1);
		session.TryMarkStorageReleased(released).ShouldBeTrue();
		session.StorageReleasedAt.ShouldBe(released);
		session.TryMarkStorageReleased(released).ShouldBeFalse();
	}

	[Fact]
	public void RecordingVersionCopiesSessionEvidence()
	{
		var session = NewSession();
		var digest = session.Declaration.ExpectedDigest;
		var creator = Guid.NewGuid();
		var version = RecordingVersion.FromCompletedUpload(Guid.NewGuid(), session, digest, "etag", creator, Created);
		version.MatchId.ShouldBe(session.MatchId);
		version.TeamId.ShouldBe(session.TeamId);
		version.UploadSessionId.ShouldBe(session.Id);
		version.ObjectKey.ShouldBe(session.ObjectKey);
		version.TotalSizeBytes.ShouldBe(10);
		version.PartSizeBytes.ShouldBe(10);
		version.PartCount.ShouldBe(1);
		version.ContentDigest.ShouldBe(digest);
		version.Descriptor.ShouldBeSameAs(session.Descriptor);
		version.StorageETag.ShouldBe("etag");
		version.CreatedBy.ShouldBe(creator);
	}
}
