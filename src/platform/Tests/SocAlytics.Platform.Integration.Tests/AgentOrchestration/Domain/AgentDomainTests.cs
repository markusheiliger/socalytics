using Shouldly;
using SocAlytics.Platform.Domain.AgentOrchestration;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.AgentOrchestration.Domain;

public sealed class AgentDomainTests
{
	[Fact]
	public void Invocation_transitions_match_the_state_machine()
	{
		var states = Enum.GetValues<AgentInvocationState>();
		var allowed = new HashSet<(AgentInvocationState, AgentInvocationState)>
		{
			(AgentInvocationState.Queued, AgentInvocationState.Running),
			(AgentInvocationState.Queued, AgentInvocationState.Failed),
			(AgentInvocationState.Running, AgentInvocationState.Succeeded),
			(AgentInvocationState.Running, AgentInvocationState.Queued),
			(AgentInvocationState.Running, AgentInvocationState.Failed),
			(AgentInvocationState.Queued, AgentInvocationState.LifecycleSuppressed),
			(AgentInvocationState.Running, AgentInvocationState.LifecycleSuppressed),
		};

		foreach (var from in states)
		{
			foreach (var to in states)
			{
				AgentStateTransitions.IsAllowed(from, to).ShouldBe(allowed.Contains((from, to)), $"{from} -> {to}");
			}
		}
	}

	[Fact]
	public void Attempt_transitions_match_the_state_machine()
	{
		AgentStateTransitions.IsAllowed(AgentAttemptState.Claimed, AgentAttemptState.Active).ShouldBeTrue();
		AgentStateTransitions.IsAllowed(AgentAttemptState.Active, AgentAttemptState.Active).ShouldBeTrue();
		AgentStateTransitions.IsAllowed(AgentAttemptState.Active, AgentAttemptState.Completed).ShouldBeTrue();
		AgentStateTransitions.IsAllowed(AgentAttemptState.Active, AgentAttemptState.Failed).ShouldBeTrue();
		AgentStateTransitions.IsAllowed(AgentAttemptState.Active, AgentAttemptState.Expired).ShouldBeTrue();
		AgentStateTransitions.IsAllowed(AgentAttemptState.Active, AgentAttemptState.Fenced).ShouldBeTrue();
		AgentStateTransitions.IsAllowed(AgentAttemptState.Completed, AgentAttemptState.Active).ShouldBeFalse();
		AgentStateTransitions.IsAllowed(AgentAttemptState.Expired, AgentAttemptState.Completed).ShouldBeFalse();
		AgentStateTransitions.IsAllowed(AgentAttemptState.Fenced, AgentAttemptState.Active).ShouldBeFalse();
	}

	[Fact]
	public void Persisted_names_round_trip()
	{
		foreach (var state in Enum.GetValues<AgentInvocationState>())
		{
			AgentPersistedNames.TryParseInvocationState(AgentPersistedNames.ToPersistedName(state), out var parsed).ShouldBeTrue();
			parsed.ShouldBe(state);
			AgentPersistedNames.TryParseInvocationState(AgentPersistedNames.ToWireName(state), out parsed).ShouldBeTrue();
			parsed.ShouldBe(state);
		}

		foreach (var state in Enum.GetValues<AgentAttemptState>())
		{
			AgentPersistedNames.TryParseAttemptState(AgentPersistedNames.ToPersistedName(state), out var parsed).ShouldBeTrue();
			parsed.ShouldBe(state);
		}

		foreach (var reason in Enum.GetValues<AgentFailureReason>())
		{
			AgentPersistedNames.TryParseFailureReason(AgentPersistedNames.ToPersistedName(reason), out var parsed).ShouldBeTrue();
			parsed.ShouldBe(reason);
		}

		foreach (var state in Enum.GetValues<AgentConversationState>())
		{
			AgentPersistedNames.TryParseConversationState(AgentPersistedNames.ToPersistedName(state), out var parsed).ShouldBeTrue();
			parsed.ShouldBe(state);
		}

		AgentPersistedNames.ToPersistedName(AgentConversationState.Archived).ShouldBe("archived");
		AgentPersistedNames.ToPersistedName(AgentInvocationState.LifecycleSuppressed).ShouldBe("lifecycle-suppressed");
		AgentPersistedNames.ToWireName(AgentInvocationState.LifecycleSuppressed).ShouldBe("lifecycle-suppressed");
		AgentStateTransitions.IsTerminal(AgentInvocationState.LifecycleSuppressed).ShouldBeTrue();
		AgentPersistedNames.AttemptEndReasonLifecycleSuppressed.ShouldBe("lifecycle-suppressed");
		AgentPersistedNames.TryParseConversationState("restored", out _).ShouldBeFalse();
		AgentPersistedNames.ToWireName(AgentInvocationState.Queued).ShouldBe("accepted/queued");
		AgentPersistedNames.ToWireName(AgentInvocationState.Running).ShouldBe("claimed/running");
		AgentPersistedNames.ToPersistedName(AgentStaleDisposition.AttemptNotCurrent).ShouldBe("attempt-not-current");
		AgentPersistedNames.ToPersistedName(AgentStaleDisposition.ScopeLost).ShouldBe("scope-lost");
		AgentPersistedNames.ToPersistedName(AgentRoute.UserToSpecialist).ShouldBe("user-to-specialist");
		AgentPersistedNames.TryParseRoute("coach-to-specialist", out _).ShouldBeFalse();
		AgentPersistedNames.TryParseInvocationState("claimed", out _).ShouldBeFalse();
	}

	[Fact]
	public void Development_policy_has_the_research_values()
	{
		var policy = ExecutionPolicy.Development;
		policy.Version.ShouldBe("direct-invocation/1");
		policy.MaxAttempts.ShouldBe(3);
		policy.LeaseSeconds.ShouldBe(30);
		policy.MaxAttemptSeconds.ShouldBe(300);
		policy.HistoryTurns.ShouldBe(10);
		policy.MaxQuestionCharacters.ShouldBe(4_000);
		policy.MaxAdviceCharacters.ShouldBe(16_000);
		policy.MaxCheckpointNoteCharacters.ShouldBe(1_000);
		policy.MaxUnfinishedInvocationsPerOwner.ShouldBe(5);
		policy.SuggestedPollingSeconds.ShouldBe(2);
		policy.HasAttemptsRemaining(2).ShouldBeTrue();
		policy.HasAttemptsRemaining(3).ShouldBeFalse();
	}

	[Fact]
	public void Policy_refuses_non_positive_values()
	{
		ExecutionPolicy.TryCreate(0, 30, 300, 10, 4_000, 16_000, 1_000, 5, 2, out var policy).ShouldBeFalse();
		policy.ShouldBeNull();
		ExecutionPolicy.TryCreate(3, 30, 300, -1, 4_000, 16_000, 1_000, 5, 2, out _).ShouldBeFalse();
		ExecutionPolicy.TryCreate(3, 30, 300, 0, 4_000, 16_000, 1_000, 5, 2, out _).ShouldBeTrue();
	}

	[Fact]
	public void Catalog_has_six_specialists_and_three_coaches()
	{
		AgentCatalogIds.Specialists.Count.ShouldBe(6);
		AgentCatalogIds.Specialists.ShouldAllBe(id => id.StartsWith(AgentCatalogIds.SpecialistPrefix, StringComparison.Ordinal));
		AgentCatalogIds.SpecialistDomains.Values.ShouldAllBe(domain => !string.IsNullOrWhiteSpace(domain));
		AgentCatalogIds.Coaches.Count.ShouldBe(3);
		AgentCatalogIds.IsSpecialist("specialist-tactics").ShouldBeTrue();
		AgentCatalogIds.IsSpecialist("coach-pep-guardiola").ShouldBeFalse();
		AgentCatalogIds.IsCoach("coach-jose-mourinho").ShouldBeTrue();
		AgentCatalogIds.IsSpecialist(null).ShouldBeFalse();
	}
}
