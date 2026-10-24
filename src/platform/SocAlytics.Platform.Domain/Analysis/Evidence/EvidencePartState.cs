namespace SocAlytics.Platform.Domain.Analysis.Evidence;

public enum EvidencePartState
{
	Available,
	Materializing,
	Rematerializing,
	Failed,
	NotCovered,
}

public static class EvidencePartStates
{
	public static string ToWireName(this EvidencePartState state) => state switch
	{
		EvidencePartState.Available => "available",
		EvidencePartState.Materializing => "materializing",
		EvidencePartState.Rematerializing => "rematerializing",
		EvidencePartState.Failed => "failed",
		EvidencePartState.NotCovered => "not-covered",
		_ => throw new ArgumentOutOfRangeException(nameof(state), state, null),
	};
}
