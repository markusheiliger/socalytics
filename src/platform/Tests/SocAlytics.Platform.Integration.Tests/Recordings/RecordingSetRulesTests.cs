using Shouldly;
using SocAlytics.Platform.Domain.Recordings;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Recordings;

public sealed class RecordingSetRulesTests
{
	private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

	private static (Guid Rv, Guid Map) NewPair() => (Guid.NewGuid(), Guid.NewGuid());

	private static Dictionary<Guid, IReadOnlySet<Guid>> Known(IEnumerable<(Guid Rv, Guid Map)> pairs) =>
		pairs.ToDictionary(p => p.Rv, p => (IReadOnlySet<Guid>)new HashSet<Guid> { p.Map });

	private static bool Create(
		IReadOnlyList<(Guid, Guid)> requested,
		IReadOnlyDictionary<Guid, IReadOnlySet<Guid>> known,
		int max,
		out RecordingSetVersion? set,
		out IReadOnlyList<RecordingSetViolation> violations) =>
		RecordingSetVersion.TryCreate(Guid.NewGuid(), Guid.NewGuid(), Now, requested, known, max, out set, out violations);

	[Fact]
	public void EmptyIsRejected()
	{
		Create([], Known([]), 100, out var set, out var v).ShouldBeFalse();
		set.ShouldBeNull();
		v.Single().Code.ShouldBe("recording-set-empty");
	}

	[Fact]
	public void MoreThanMaximumIsTooLargeAndExactMaximumIsAccepted()
	{
		var pairs = Enumerable.Range(0, 101).Select(_ => NewPair()).ToList();
		var known = Known(pairs);

		Create(pairs.Cast<(Guid, Guid)>().ToList(), known, 100, out _, out var v).ShouldBeFalse();
		v.Single().Code.ShouldBe("recording-set-too-large");

		Create(pairs.Take(100).Cast<(Guid, Guid)>().ToList(), known, 100, out var set, out _).ShouldBeTrue();
		set!.MemberCount.ShouldBe(100);
	}

	[Fact]
	public void FourMembersWithMaximumThreeAreRejected()
	{
		var pairs = Enumerable.Range(0, 4).Select(_ => NewPair()).ToList();
		Create(pairs.Cast<(Guid, Guid)>().ToList(), Known(pairs), 3, out _, out var v).ShouldBeFalse();
		v.Single().Code.ShouldBe("recording-set-too-large");
	}

	[Fact]
	public void UnknownMemberIsReportedByIndex()
	{
		var a = NewPair();
		var unknown = NewPair();
		Create([a, unknown], Known([a]), 100, out _, out var v).ShouldBeFalse();
		var violation = v.Single();
		violation.Code.ShouldBe("recording-set-member-invalid");
		violation.Field.ShouldBe("members[1]");
	}

	[Fact]
	public void MappingBoundToAnotherRecordingVersionIsInvalidWithSameViolationAsUnknown()
	{
		var a = NewPair();
		var b = NewPair();
		Create([(a.Rv, b.Map)], Known([a, b]), 100, out _, out var v).ShouldBeFalse();
		v.Single().ShouldBe(new RecordingSetViolation(
			"recording-set-member-invalid",
			"members[0]",
			"The recording version and timeline mapping are not valid for this match."));

		Create([NewPair()], Known([a]), 100, out _, out var unknown).ShouldBeFalse();
		unknown.Single().Message.ShouldBe(v.Single().Message);
	}

	[Fact]
	public void DuplicateRecordingVersionIsRejected()
	{
		var a = NewPair();
		Create([a, a], Known([a]), 100, out _, out var v).ShouldBeFalse();
		var violation = v.Single();
		violation.Code.ShouldBe("recording-set-duplicate-recording");
		violation.Field.ShouldBe("members[1]");
	}

	[Fact]
	public void ValidOrderedSetPreservesOrder()
	{
		var pairs = Enumerable.Range(0, 3).Select(_ => NewPair()).ToList();
		var order = new[] { pairs[2], pairs[0], pairs[1] };

		Create(order.Cast<(Guid, Guid)>().ToList(), Known(pairs), 100, out var set, out _).ShouldBeTrue();

		set!.Members.Select(m => m.Position).ShouldBe([1, 2, 3]);
		set.Members.Select(m => m.RecordingVersionId).ShouldBe(order.Select(p => p.Rv));
		set.Members.Select(m => m.TimelineMappingId).ShouldBe(order.Select(p => p.Map));
		set.FinalizedAt.ShouldBe(Now);
	}
}
