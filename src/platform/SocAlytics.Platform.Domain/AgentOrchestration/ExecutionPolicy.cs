namespace SocAlytics.Platform.Domain.AgentOrchestration;

public sealed record ExecutionPolicy
{
	public const string DirectInvocationVersion = "direct-invocation/1";

	public ExecutionPolicy(
		int maxAttempts,
		int leaseSeconds,
		int maxAttemptSeconds,
		int historyTurns,
		int maxQuestionCharacters,
		int maxAdviceCharacters,
		int maxCheckpointNoteCharacters,
		int maxUnfinishedInvocationsPerOwner,
		int suggestedPollingSeconds)
	{
		ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxAttempts, 0);
		ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(leaseSeconds, 0);
		ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxAttemptSeconds, 0);
		ArgumentOutOfRangeException.ThrowIfNegative(historyTurns);
		ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxQuestionCharacters, 0);
		ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxAdviceCharacters, 0);
		ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxCheckpointNoteCharacters, 0);
		ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxUnfinishedInvocationsPerOwner, 0);
		ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(suggestedPollingSeconds, 0);
		MaxAttempts = maxAttempts;
		LeaseSeconds = leaseSeconds;
		MaxAttemptSeconds = maxAttemptSeconds;
		HistoryTurns = historyTurns;
		MaxQuestionCharacters = maxQuestionCharacters;
		MaxAdviceCharacters = maxAdviceCharacters;
		MaxCheckpointNoteCharacters = maxCheckpointNoteCharacters;
		MaxUnfinishedInvocationsPerOwner = maxUnfinishedInvocationsPerOwner;
		SuggestedPollingSeconds = suggestedPollingSeconds;
	}

	// Development values of the research decision R5.
	public static ExecutionPolicy Development { get; } = new(3, 30, 300, 10, 4_000, 16_000, 1_000, 5, 2);

	public string Version => DirectInvocationVersion;

	public int MaxAttempts { get; }

	public int LeaseSeconds { get; }

	public int MaxAttemptSeconds { get; }

	public int HistoryTurns { get; }

	public int MaxQuestionCharacters { get; }

	public int MaxAdviceCharacters { get; }

	public int MaxCheckpointNoteCharacters { get; }

	public int MaxUnfinishedInvocationsPerOwner { get; }

	public int SuggestedPollingSeconds { get; }

	public static bool TryCreate(
		int maxAttempts,
		int leaseSeconds,
		int maxAttemptSeconds,
		int historyTurns,
		int maxQuestionCharacters,
		int maxAdviceCharacters,
		int maxCheckpointNoteCharacters,
		int maxUnfinishedInvocationsPerOwner,
		int suggestedPollingSeconds,
		out ExecutionPolicy? policy)
	{
		try
		{
			policy = new ExecutionPolicy(
				maxAttempts,
				leaseSeconds,
				maxAttemptSeconds,
				historyTurns,
				maxQuestionCharacters,
				maxAdviceCharacters,
				maxCheckpointNoteCharacters,
				maxUnfinishedInvocationsPerOwner,
				suggestedPollingSeconds);
			return true;
		}
		catch (ArgumentOutOfRangeException)
		{
			policy = null;
			return false;
		}
	}

	public bool HasAttemptsRemaining(int attemptsUsed)
	{
		ArgumentOutOfRangeException.ThrowIfNegative(attemptsUsed);
		return attemptsUsed < MaxAttempts;
	}
}
