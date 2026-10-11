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
}
