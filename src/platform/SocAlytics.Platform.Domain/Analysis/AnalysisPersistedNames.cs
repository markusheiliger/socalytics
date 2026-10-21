namespace SocAlytics.Platform.Domain.Analysis;

public static class AnalysisPersistedNames
{
	public static string ToPersistedName(ExecutionScope scope) => scope switch
	{
		ExecutionScope.Segment => "segment",
		ExecutionScope.Match => "match",
		_ => throw new ArgumentOutOfRangeException(nameof(scope)),
	};

	public static bool TryParseExecutionScope(string? value, out ExecutionScope scope)
	{
		switch (value)
		{
			case "segment":
				scope = ExecutionScope.Segment;
				return true;
			case "match":
				scope = ExecutionScope.Match;
				return true;
			default:
				scope = default;
				return false;
		}
	}

	public static string ToPersistedName(CapabilityTier tier) => tier switch
	{
		CapabilityTier.LowLevel => "low-level",
		CapabilityTier.HighLevel => "high-level",
		_ => throw new ArgumentOutOfRangeException(nameof(tier)),
	};

	public static bool TryParseCapabilityTier(string? value, out CapabilityTier tier)
	{
		switch (value)
		{
			case "low-level":
				tier = CapabilityTier.LowLevel;
				return true;
			case "high-level":
				tier = CapabilityTier.HighLevel;
				return true;
			default:
				tier = default;
				return false;
		}
	}

	public static string ToPersistedName(CancellationCause cause) => cause switch
	{
		CancellationCause.Member => "member",
		CancellationCause.Superseded => "superseded",
		_ => throw new ArgumentOutOfRangeException(nameof(cause)),
	};

	public static bool TryParseCancellationCause(string? value, out CancellationCause cause)
	{
		switch (value)
		{
			case "member":
				cause = CancellationCause.Member;
				return true;
			case "superseded":
				cause = CancellationCause.Superseded;
				return true;
			default:
				cause = default;
				return false;
		}
	}

	public const string CoverageModeComplete = "complete";

	public const string CoverageModePartial = "partial";

	public const string UpstreamCoverageBelowMinimum = "upstream-coverage-below-minimum";

	public static string ToPersistedName(CoverageMode mode) => mode switch
	{
		CoverageMode.Complete => CoverageModeComplete,
		CoverageMode.Partial => CoverageModePartial,
		_ => throw new ArgumentOutOfRangeException(nameof(mode)),
	};

	public static bool TryParseCoverageMode(string? value, out CoverageMode mode)
	{
		switch (value)
		{
			case CoverageModeComplete:
				mode = CoverageMode.Complete;
				return true;
			case CoverageModePartial:
				mode = CoverageMode.Partial;
				return true;
			default:
				mode = default;
				return false;
		}
	}

	public static string ToPersistedName(DependencyKind kind) => kind switch
	{
		DependencyKind.Required => "required",
		DependencyKind.Optional => "optional",
		DependencyKind.Conditional => "conditional",
		_ => throw new ArgumentOutOfRangeException(nameof(kind)),
	};

	public static bool TryParseDependencyKind(string? value, out DependencyKind kind)
	{
		switch (value)
		{
			case "required":
				kind = DependencyKind.Required;
				return true;
			case "optional":
				kind = DependencyKind.Optional;
				return true;
			case "conditional":
				kind = DependencyKind.Conditional;
				return true;
			default:
				kind = default;
				return false;
		}
	}
}
