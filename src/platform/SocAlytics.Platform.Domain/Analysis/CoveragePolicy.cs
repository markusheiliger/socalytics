namespace SocAlytics.Platform.Domain.Analysis;

public enum CoverageMode
{
	Complete,
	Partial,
}

/// <summary>
/// How much of a segment-scoped upstream node must be accepted before a match-scoped dependent may start.
/// It is effective only on a partial barrier edge; elsewhere it is evaluated as complete.
/// </summary>
public sealed record CoveragePolicy
{
	public static CoveragePolicy Complete { get; } = new();

	public CoverageMode Mode { get; }

	public decimal? MinimumAcceptedFraction { get; }

	private CoveragePolicy()
	{
		Mode = CoverageMode.Complete;
	}

	private CoveragePolicy(decimal? minimumAcceptedFraction)
	{
		Mode = CoverageMode.Partial;
		MinimumAcceptedFraction = minimumAcceptedFraction;
	}

	public static bool IsValidFraction(decimal fraction) => fraction > 0m && fraction <= 1m;

	public static CoveragePolicy Partial(decimal? minimumAcceptedFraction = null)
	{
		if (minimumAcceptedFraction is { } fraction && !IsValidFraction(fraction))
		{
			throw new ArgumentOutOfRangeException(nameof(minimumAcceptedFraction), "The minimum accepted fraction must satisfy 0 < f <= 1.");
		}

		return new CoveragePolicy(minimumAcceptedFraction);
	}

	/// <summary>The number of accepted segments needed: max(1, ceil(f × expected)), or 1 without a fraction.</summary>
	public int RequiredAccepted(int expected)
	{
		if (MinimumAcceptedFraction is not { } fraction)
		{
			return 1;
		}

		return Math.Max(1, (int)decimal.Ceiling(fraction * expected));
	}
}
