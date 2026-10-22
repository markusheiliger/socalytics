namespace SocAlytics.Platform.Domain.Segments;

public enum MaterializedSegmentState
{
	Absent,
	Materializing,
	Available,
	Failed,
}

public static class MaterializedSegmentStates
{
	public static string ToPersistedName(MaterializedSegmentState state) => state switch
	{
		MaterializedSegmentState.Absent => "absent",
		MaterializedSegmentState.Materializing => "materializing",
		MaterializedSegmentState.Available => "available",
		MaterializedSegmentState.Failed => "failed",
		_ => throw new ArgumentOutOfRangeException(nameof(state)),
	};

	public static bool TryParse(string? value, out MaterializedSegmentState state)
	{
		switch (value)
		{
			case "absent":
				state = MaterializedSegmentState.Absent;
				return true;
			case "materializing":
				state = MaterializedSegmentState.Materializing;
				return true;
			case "available":
				state = MaterializedSegmentState.Available;
				return true;
			case "failed":
				state = MaterializedSegmentState.Failed;
				return true;
			default:
				state = default;
				return false;
		}
	}
}
