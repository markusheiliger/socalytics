namespace SocAlytics.Platform.Domain.AgentOrchestration.Catalog;

public enum AgentCatalogRecordKind
{
	PromptTemplate,
	ModelSelection,
	ToolPolicy,
	SafetyPolicy,
	DimensionSet,
	PhilosophyProfile,
	ModifierPolicy,
}

public static class AgentCatalogRecordKinds
{
	public static IReadOnlyList<AgentCatalogRecordKind> All { get; } = Enum.GetValues<AgentCatalogRecordKind>();

	public static string ToName(AgentCatalogRecordKind kind) => kind switch
	{
		AgentCatalogRecordKind.PromptTemplate => "prompt-template",
		AgentCatalogRecordKind.ModelSelection => "model-selection",
		AgentCatalogRecordKind.ToolPolicy => "tool-policy",
		AgentCatalogRecordKind.SafetyPolicy => "safety-policy",
		AgentCatalogRecordKind.DimensionSet => "dimension-set",
		AgentCatalogRecordKind.PhilosophyProfile => "philosophy-profile",
		AgentCatalogRecordKind.ModifierPolicy => "modifier-policy",
		_ => throw new ArgumentOutOfRangeException(nameof(kind)),
	};

	public static bool TryParse(string? value, out AgentCatalogRecordKind kind)
	{
		foreach (var candidate in All)
		{
			if (string.Equals(ToName(candidate), value, StringComparison.Ordinal))
			{
				kind = candidate;
				return true;
			}
		}

		kind = default;
		return false;
	}
}
