namespace SocAlytics.Platform.Domain.AgentOrchestration.Catalog;

public enum AgentVersionState
{
	Draft,
	Approved,
	Retired,
}

public static class AgentVersionStates
{
	public static bool IsAllowed(AgentVersionState from, AgentVersionState to) => (from, to) switch
	{
		(AgentVersionState.Draft, AgentVersionState.Approved) => true,
		(AgentVersionState.Approved, AgentVersionState.Retired) => true,
		_ => false,
	};

	public static string ToPersistedName(AgentVersionState state) => state switch
	{
		AgentVersionState.Draft => "draft",
		AgentVersionState.Approved => "approved",
		AgentVersionState.Retired => "retired",
		_ => throw new ArgumentOutOfRangeException(nameof(state)),
	};

	public static bool TryParse(string? value, out AgentVersionState state)
	{
		switch (value)
		{
			case "draft":
				state = AgentVersionState.Draft;
				return true;
			case "approved":
				state = AgentVersionState.Approved;
				return true;
			case "retired":
				state = AgentVersionState.Retired;
				return true;
			default:
				state = default;
				return false;
		}
	}
}
