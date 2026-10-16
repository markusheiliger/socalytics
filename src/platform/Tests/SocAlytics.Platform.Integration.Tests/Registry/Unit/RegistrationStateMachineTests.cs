using System.Text.Json;
using Shouldly;
using SocAlytics.Platform.Domain.Registry;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Registry.Unit;

public sealed class RegistrationStateMachineTests
{
	private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
	private static readonly RegistrationWindows Windows = RegistrationWindows.Default;
	private static readonly Guid Member = Guid.NewGuid();
	private static readonly byte[] Hash = new byte[32];

	private static AnalystManagerRegistration New()
	{
		var jwk = JsonDocument.Parse("""{"kty":"EC","crv":"P-256","x":"f83OJ3D2xF1Bg8vub9tLe1gHMzV76e8Tus9uPHvRVEU","y":"x_FEzRu9m36HLN_tue659LNpXW6pCyStikYjKIWI5a0"}""").RootElement;
		DeviceKeyThumbprint.TryCreate(jwk, out var thumbprint).ShouldBeTrue();
		DeviceMetadata.TryCreate("linux", "6", "x64", "1.0", "software", null, null, out var metadata, out _).ShouldBeTrue();
		return AnalystManagerRegistration.Start(
			Guid.NewGuid(), "stamp", thumbprint!, jwk.GetRawText(), new string('n', 16), metadata!, "127.0.0.1", "corr", T0, Windows);
	}

	private static AnalystManagerRegistration InState(RegistrationState target)
	{
		var r = New();
		if (target == RegistrationState.Pairing) return r;
		r.Submit(Member, T0).Succeeded.ShouldBeTrue();
		if (target == RegistrationState.PendingApproval) return r;
		if (target == RegistrationState.Rejected) { r.Reject(Member, "no", T0).Succeeded.ShouldBeTrue(); return r; }
		if (target == RegistrationState.Expired) { r.Expire("approval-window-elapsed", T0).Succeeded.ShouldBeTrue(); return r; }
		r.Approve(Member, T0).Succeeded.ShouldBeTrue();
		if (target == RegistrationState.Approved) return r;
		r.Activate(Guid.NewGuid(), T0).Succeeded.ShouldBeTrue();
		if (target == RegistrationState.Active) return r;
		if (target == RegistrationState.Revoked) { r.Revoke(Member, "x", T0).Succeeded.ShouldBeTrue(); return r; }
		r.BeginUnregistration(T0).Succeeded.ShouldBeTrue();
		if (target == RegistrationState.Unregistering) return r;
		r.CompleteUnregistration(T0).Succeeded.ShouldBeTrue();
		return r;
	}

	private static readonly (string Name, Func<AnalystManagerRegistration, RegistrationTransitionResult> Run, RegistrationState From, RegistrationState To)[] Allowed =
	[
		("Submit", r => r.Submit(Member, T0), RegistrationState.Pairing, RegistrationState.PendingApproval),
		("Approve", r => r.Approve(Member, T0), RegistrationState.PendingApproval, RegistrationState.Approved),
		("Reject", r => r.Reject(Member, "reason", T0), RegistrationState.PendingApproval, RegistrationState.Rejected),
		("Activate", r => r.Activate(Guid.NewGuid(), T0), RegistrationState.Approved, RegistrationState.Active),
		("ExpirePairing", r => r.Expire("pairing-window-elapsed", T0), RegistrationState.Pairing, RegistrationState.Expired),
		("ExpirePending", r => r.Expire("approval-window-elapsed", T0), RegistrationState.PendingApproval, RegistrationState.Expired),
		("ExpireApproved", r => r.Expire("activation-window-elapsed", T0), RegistrationState.Approved, RegistrationState.Expired),
		("Begin", r => r.BeginUnregistration(T0), RegistrationState.Active, RegistrationState.Unregistering),
		("Complete", r => r.CompleteUnregistration(T0), RegistrationState.Unregistering, RegistrationState.Unregistered),
		("RevokeActive", r => r.Revoke(Member, "r", T0), RegistrationState.Active, RegistrationState.Revoked),
		("RevokeUnregistering", r => r.Revoke(Member, "r", T0), RegistrationState.Unregistering, RegistrationState.Revoked),
	];

	[Fact]
	public void EveryAllowedTransitionSucceedsAndEveryOtherSourceIsForbidden()
	{
		var groups = Allowed.GroupBy(a => a.Name.StartsWith("Expire") ? "Expire" : a.Name.StartsWith("Revoke") ? "Revoke" : a.Name);
		foreach (var group in groups)
		{
			var sources = group.Select(a => a.From).ToHashSet();
			foreach (var (name, run, from, to) in group)
			{
				var r = InState(from);
				run(r).Succeeded.ShouldBeTrue(name);
				r.State.ShouldBe(to, name);
			}

			var any = group.First().Run;
			foreach (var other in Enum.GetValues<RegistrationState>().Where(o => !sources.Contains(o)))
			{
				if (group.Key == "Begin" && other == RegistrationState.Unregistering) continue;
				any(InState(other)).Violation.ShouldBe(RegistrationTransitionResult.InvalidStateTransition, $"{group.Key} from {other}");
			}
		}
	}

	[Fact]
	public void TerminalStatesNeverReachActive()
	{
		foreach (var terminal in new[] { RegistrationState.Unregistered, RegistrationState.Rejected, RegistrationState.Expired, RegistrationState.Revoked })
		{
			var r = InState(terminal);
			r.Activate(Guid.NewGuid(), T0).Violation.ShouldBe(RegistrationTransitionResult.InvalidStateTransition);
			r.State.ShouldBe(terminal);
			if (terminal is RegistrationState.Rejected or RegistrationState.Expired) r.ManagerId.ShouldBeNull();
		}
	}

	[Fact]
	public void BeginUnregistrationIsIdempotent()
	{
		var r = InState(RegistrationState.Unregistering);
		var started = r.UnregistrationStartedAt;
		r.BeginUnregistration(T0.AddHours(1)).Succeeded.ShouldBeTrue();
		r.State.ShouldBe(RegistrationState.Unregistering);
		r.UnregistrationStartedAt.ShouldBe(started);
	}

	[Fact]
	public void WindowBoundariesAreExclusive()
	{
		var r = New();
		r.Submit(Member, T0 + Windows.Pairing).Violation.ShouldBe(RegistrationTransitionResult.RegistrationExpired);
		r.Submit(Member, T0 + Windows.Pairing - TimeSpan.FromTicks(1)).Succeeded.ShouldBeTrue();

		r.Approve(Member, r.ApprovalExpiresAt!.Value).Violation.ShouldBe(RegistrationTransitionResult.RegistrationExpired);
		r.Approve(Member, r.ApprovalExpiresAt!.Value.AddTicks(-1)).Succeeded.ShouldBeTrue();

		r.IssueChallenge(Hash, r.ActivationExpiresAt!.Value).Violation.ShouldBe(RegistrationTransitionResult.RegistrationExpired);
		var now = r.ActivationExpiresAt!.Value.AddTicks(-1);
		r.IssueChallenge(Hash, now).Succeeded.ShouldBeTrue();
		r.ChallengeExpiresAt.ShouldBe(now + Windows.ChallengeLifetime);
		r.Activate(Guid.NewGuid(), r.ActivationExpiresAt!.Value).Violation.ShouldBe(RegistrationTransitionResult.RegistrationExpired);
		r.Activate(Guid.NewGuid(), now).Succeeded.ShouldBeTrue();
		r.ChallengeHash.ShouldBeNull();
	}

	[Fact]
	public void MismatchLimitExpiresPairing()
	{
		var r = New();
		r.RecordFingerprintMismatch(3, T0).Succeeded.ShouldBeTrue();
		r.RecordFingerprintMismatch(3, T0).Succeeded.ShouldBeTrue();
		r.State.ShouldBe(RegistrationState.Pairing);
		r.RecordFingerprintMismatch(3, T0).Succeeded.ShouldBeTrue();
		r.State.ShouldBe(RegistrationState.Expired);
		r.EndReason.ShouldBe("fingerprint-mismatch-limit");
		r.FingerprintMismatchCount.ShouldBe(3);
		r.RecordFingerprintMismatch(3, T0).Violation.ShouldBe(RegistrationTransitionResult.InvalidStateTransition);
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("   ")]
	public void RejectAndRevokeRequireAReason(string? reason)
	{
		InState(RegistrationState.PendingApproval).Reject(Member, reason, T0).Succeeded.ShouldBeFalse();
		InState(RegistrationState.Active).Revoke(Member, reason, T0).Succeeded.ShouldBeFalse();
		InState(RegistrationState.Active).Revoke(Member, new string('a', 501), T0).Succeeded.ShouldBeFalse();
		InState(RegistrationState.Active).Revoke(Member, new string('a', 500), T0).Succeeded.ShouldBeTrue();
	}

	[Fact]
	public void ApiNamesRoundTrip()
	{
		RegistrationStateExtensions.ToApiName(RegistrationState.PendingApproval).ShouldBe("pending_approval");
		foreach (var s in Enum.GetValues<RegistrationState>())
		{
			RegistrationStateExtensions.TryParseApiName(s.ToApiName(), out var parsed).ShouldBeTrue();
			parsed.ShouldBe(s);
		}
	}
}
