namespace SocAlytics.Platform.Domain.AgentOrchestration;

public static class AgentPersistedNames
{
	// Attempts of a suppressed invocation end Fenced with this end reason.
	public const string AttemptEndReasonLifecycleSuppressed = "lifecycle-suppressed";

	// The purge copies of a deleted conversation's content.
	public const string PurgeCopyPrimary = "primary";
	public const string PurgeCopyIndex = "index";

	public const string PurgeStatePending = "pending";
	public const string PurgeStateCompleted = "completed";
	public const string PurgeStateUnresolved = "unresolved";

	public const string PurgeOutcomePurged = "purged";
	public const string PurgeOutcomeNotPresent = "not-present";

	public static string ToPersistedName(AgentConversationState state) => state switch
	{
		AgentConversationState.Active => "active",
		AgentConversationState.Archived => "archived",
		AgentConversationState.Deleted => "deleted",
		_ => throw new ArgumentOutOfRangeException(nameof(state)),
	};

	public static bool TryParseConversationState(string? value, out AgentConversationState state)
	{
		switch (value)
		{
			case "active":
				state = AgentConversationState.Active;
				return true;
			case "archived":
				state = AgentConversationState.Archived;
				return true;
			case "deleted":
				state = AgentConversationState.Deleted;
				return true;
			default:
				state = default;
				return false;
		}
	}

	public static string ToPersistedName(AgentInvocationState state) => state switch
	{
		AgentInvocationState.Queued => "queued",
		AgentInvocationState.Running => "running",
		AgentInvocationState.Succeeded => "succeeded",
		AgentInvocationState.Failed => "failed",
		AgentInvocationState.LifecycleSuppressed => "lifecycle-suppressed",
		_ => throw new ArgumentOutOfRangeException(nameof(state)),
	};

	// The architecture's names: accepted/queued and claimed/running are one state each.
	public static string ToWireName(AgentInvocationState state) => state switch
	{
		AgentInvocationState.Queued => "accepted/queued",
		AgentInvocationState.Running => "claimed/running",
		AgentInvocationState.Succeeded => "succeeded",
		AgentInvocationState.Failed => "failed",
		AgentInvocationState.LifecycleSuppressed => "lifecycle-suppressed",
		_ => throw new ArgumentOutOfRangeException(nameof(state)),
	};

	public static bool TryParseInvocationState(string? value, out AgentInvocationState state)
	{
		switch (value)
		{
			case "queued" or "accepted/queued":
				state = AgentInvocationState.Queued;
				return true;
			case "running" or "claimed/running":
				state = AgentInvocationState.Running;
				return true;
			case "succeeded":
				state = AgentInvocationState.Succeeded;
				return true;
			case "failed":
				state = AgentInvocationState.Failed;
				return true;
			case "lifecycle-suppressed":
				state = AgentInvocationState.LifecycleSuppressed;
				return true;
			default:
				state = default;
				return false;
		}
	}

	public static string ToPersistedName(AgentAttemptState state) => state switch
	{
		AgentAttemptState.Claimed => "claimed",
		AgentAttemptState.Active => "active",
		AgentAttemptState.Completed => "completed",
		AgentAttemptState.Failed => "failed",
		AgentAttemptState.Expired => "expired",
		AgentAttemptState.Fenced => "fenced",
		_ => throw new ArgumentOutOfRangeException(nameof(state)),
	};

	public static bool TryParseAttemptState(string? value, out AgentAttemptState state)
	{
		switch (value)
		{
			case "claimed":
				state = AgentAttemptState.Claimed;
				return true;
			case "active":
				state = AgentAttemptState.Active;
				return true;
			case "completed":
				state = AgentAttemptState.Completed;
				return true;
			case "failed":
				state = AgentAttemptState.Failed;
				return true;
			case "expired":
				state = AgentAttemptState.Expired;
				return true;
			case "fenced":
				state = AgentAttemptState.Fenced;
				return true;
			default:
				state = default;
				return false;
		}
	}

	public static string ToPersistedName(AgentFailureReason reason) => reason switch
	{
		AgentFailureReason.AttemptsExhausted => "attempts-exhausted",
		AgentFailureReason.ExecutionFailed => "execution-failed",
		AgentFailureReason.ScopeLost => "scope-lost",
		AgentFailureReason.AgentNotAvailable => "agent-not-available",
		_ => throw new ArgumentOutOfRangeException(nameof(reason)),
	};

	public static bool TryParseFailureReason(string? value, out AgentFailureReason reason)
	{
		switch (value)
		{
			case "attempts-exhausted":
				reason = AgentFailureReason.AttemptsExhausted;
				return true;
			case "execution-failed":
				reason = AgentFailureReason.ExecutionFailed;
				return true;
			case "scope-lost":
				reason = AgentFailureReason.ScopeLost;
				return true;
			case "agent-not-available":
				reason = AgentFailureReason.AgentNotAvailable;
				return true;
			default:
				reason = default;
				return false;
		}
	}

	public static string ToPersistedName(AgentStaleDisposition disposition) => disposition switch
	{
		AgentStaleDisposition.AttemptNotCurrent => "attempt-not-current",
		AgentStaleDisposition.LeaseExpired => "lease-expired",
		AgentStaleDisposition.AttemptFenced => "attempt-fenced",
		AgentStaleDisposition.InvocationEnded => "invocation-ended",
		AgentStaleDisposition.ScopeLost => "scope-lost",
		_ => throw new ArgumentOutOfRangeException(nameof(disposition)),
	};

	public static string ToPersistedName(AgentRoute route) => route switch
	{
		AgentRoute.UserToSpecialist => "user-to-specialist",
		_ => throw new ArgumentOutOfRangeException(nameof(route)),
	};

	public static bool TryParseRoute(string? value, out AgentRoute route)
	{
		if (value == "user-to-specialist")
		{
			route = AgentRoute.UserToSpecialist;
			return true;
		}

		route = default;
		return false;
	}
}
