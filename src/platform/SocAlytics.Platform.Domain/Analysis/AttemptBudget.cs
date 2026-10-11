namespace SocAlytics.Platform.Domain.Analysis;

public sealed record AttemptBudget
{
	public AttemptBudget(int maxAttempts)
	{
		ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxAttempts, 0);
		MaxAttempts = maxAttempts;
	}

	public int MaxAttempts { get; }

	public bool HasRemaining(int attemptsUsed)
	{
		ArgumentOutOfRangeException.ThrowIfNegative(attemptsUsed);
		ArgumentOutOfRangeException.ThrowIfGreaterThan(attemptsUsed, MaxAttempts);
		return attemptsUsed < MaxAttempts;
	}
}
