using System.Text.RegularExpressions;

namespace SocAlytics.Platform.Domain.AgentOrchestration.Catalog;

// The cross-record validation rules of data-model.md; every method returns all violations it finds.
public static partial class AgentCatalogRules
{
	public const int MaxPerDimensionBound = 3;
	public const int MaxPositiveMovementLimit = 10;

	public static IReadOnlyList<string> PermittedProviders { get; } = ["openai", "github-models", "simulated"];

	public static IReadOnlyList<string> PermittedContextCategories { get; } =
		["score-state", "match-period", "player-number-state", "opponent-phase-behavior", "evidence-availability"];

	public static IReadOnlyList<string> RequiredCoachSafetyRules { get; } =
		["simulation-disclosure", "non-impersonation", "non-fabrication"];

	public static IReadOnlyList<string> PromptSlots { get; } =
		["agent_id", "display_name", "philosophy_profile", "tool_policy", "evidence_context"];

	public const string ProfileSlot = "philosophy_profile";

	public static IReadOnlyList<AgentCatalogViolation> ValidateModelSelection(ModelSelectionContent model)
	{
		var violations = new List<AgentCatalogViolation>();
		if (!PermittedProviders.Contains(model.Provider, StringComparer.Ordinal))
		{
			violations.Add(new(AgentCatalogViolationCodes.ProviderNotPermitted, "/provider", $"Provider '{model.Provider}' is not permitted."));
		}

		return violations;
	}

	public static IReadOnlyList<AgentCatalogViolation> ValidateToolPolicy(ToolPolicyContent policy)
	{
		var violations = new List<AgentCatalogViolation>();
		CheckAllowlist(policy.Tools, "/tools", violations);
		return violations;
	}

	public static IReadOnlyList<AgentCatalogViolation> ValidateSafetyPolicy(SafetyPolicyContent policy)
	{
		var violations = new List<AgentCatalogViolation>();
		CheckSafetyRules(policy, "/rules", violations);
		return violations;
	}

	public static IReadOnlyList<AgentCatalogViolation> ValidatePromptTemplate(PromptTemplateContent prompt)
	{
		var violations = new List<AgentCatalogViolation>();
		var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
		foreach (Match match in SlotPattern().Matches(prompt.Text))
		{
			var slot = match.Groups["slot"].Value;
			occurrences[slot] = occurrences.GetValueOrDefault(slot) + 1;
		}

		for (var i = 0; i < prompt.Slots.Count; i++)
		{
			var slot = prompt.Slots[i];
			if (occurrences.GetValueOrDefault(slot) != 1)
			{
				violations.Add(new(AgentCatalogViolationCodes.ProfileSlotMissing, $"/slots/{i}", $"Declared slot '{slot}' must occur exactly once as {{{{{slot}}}}}."));
			}
		}

		foreach (var slot in occurrences.Keys.Where(slot => !prompt.Slots.Contains(slot, StringComparer.Ordinal)))
		{
			violations.Add(new(AgentCatalogViolationCodes.ProfileSlotNotAllowed, "/text", $"Slot '{slot}' occurs but is not declared."));
		}

		var declaresProfile = prompt.Slots.Contains(ProfileSlot, StringComparer.Ordinal);
		if (string.Equals(prompt.Group, AgentCatalogIds.GroupCoach, StringComparison.Ordinal))
		{
			if (!declaresProfile)
			{
				violations.Add(new(AgentCatalogViolationCodes.ProfileSlotMissing, "/slots", $"Coach prompts declare the '{ProfileSlot}' slot."));
			}
		}
		else if (declaresProfile)
		{
			violations.Add(new(AgentCatalogViolationCodes.ProfileSlotNotAllowed, "/slots", $"Specialist prompts do not declare the '{ProfileSlot}' slot."));
		}

		return violations;
	}

	// existingSameMajor holds the registered versions with the same id and major as the new dimension set.
	public static IReadOnlyList<AgentCatalogViolation> ValidateDimensionSet(
		DimensionSetContent set,
		IReadOnlyList<DimensionSetContent> existingSameMajor)
	{
		var violations = new List<AgentCatalogViolation>();
		var seen = new HashSet<string>(StringComparer.Ordinal);
		for (var i = 0; i < set.Dimensions.Count; i++)
		{
			var dimension = set.Dimensions[i];
			if (!seen.Add(dimension.Id))
			{
				violations.Add(new(AgentCatalogViolationCodes.SchemaViolation, $"/dimensions/{i}/id", $"Dimension id '{dimension.Id}' is not unique."));
			}

			CheckAllowlist(dimension.AcceptedEvidence, $"/dimensions/{i}/acceptedEvidence", violations);
		}

		var ids = set.Dimensions.Select(dimension => dimension.Id).ToHashSet(StringComparer.Ordinal);
		foreach (var existing in existingSameMajor)
		{
			if (!ids.SetEquals(existing.Dimensions.Select(dimension => dimension.Id)))
			{
				violations.Add(new(AgentCatalogViolationCodes.DimensionSetIdsChanged, "/dimensions", $"Version {existing.Version} of '{set.Id}' has a different set of dimension ids within the same major version."));
				break;
			}
		}

		return violations;
	}

	// existingProfileVersions holds the registered versions of the profile id.
	public static IReadOnlyList<AgentCatalogViolation> ValidateProfile(
		PhilosophyProfileContent profile,
		ResolvedArtifact<DimensionSetContent> dimensionSet,
		IReadOnlyList<string> existingProfileVersions)
	{
		var violations = new List<AgentCatalogViolation>();
		if (dimensionSet.Record is null)
		{
			violations.Add(new(
				dimensionSet.KindMismatch ? AgentCatalogViolationCodes.ArtifactKindMismatch : AgentCatalogViolationCodes.DimensionSetNotFound,
				"/dimensionSet",
				$"Dimension set '{profile.DimensionSetId}' {profile.DimensionSetVersion} does not exist."));
		}
		else
		{
			var ids = dimensionSet.Record.Dimensions.Select(dimension => dimension.Id).ToHashSet(StringComparer.Ordinal);
			foreach (var id in ids.Where(id => !profile.Weights.ContainsKey(id)))
			{
				violations.Add(new(AgentCatalogViolationCodes.MissingDimension, $"/weights/{Escape(id)}", $"Dimension '{id}' has no weight."));
			}

			foreach (var id in profile.Weights.Keys.Where(id => !ids.Contains(id)))
			{
				violations.Add(new(AgentCatalogViolationCodes.UnknownDimension, $"/weights/{Escape(id)}", $"Dimension '{id}' is not part of the dimension set."));
			}
		}

		var total = 0;
		foreach (var (id, weight) in profile.Weights)
		{
			if (weight.Weight is < 0 or > 100)
			{
				violations.Add(new(AgentCatalogViolationCodes.WeightOutOfRange, $"/weights/{Escape(id)}/weight", "A weight is an integer from 0 to 100."));
			}

			if (string.IsNullOrWhiteSpace(weight.Rationale))
			{
				violations.Add(new(AgentCatalogViolationCodes.RationaleMissing, $"/weights/{Escape(id)}/rationale", "A rationale is required."));
			}

			if (string.IsNullOrWhiteSpace(weight.Provenance))
			{
				violations.Add(new(AgentCatalogViolationCodes.RationaleMissing, $"/weights/{Escape(id)}/provenance", "A provenance is required."));
			}

			total += weight.Weight;
		}

		if (total != 100)
		{
			violations.Add(new(AgentCatalogViolationCodes.WeightsTotalNot100, "/weights", $"The weights total {total}, not 100."));
		}

		if (profile.SupersedesVersion is not null)
		{
			var valid = SemanticVersion.TryParse(profile.SupersedesVersion, out var superseded)
				&& SemanticVersion.TryParse(profile.Version, out var current)
				&& superseded < current
				&& existingProfileVersions.Contains(profile.SupersedesVersion, StringComparer.Ordinal);
			if (!valid)
			{
				violations.Add(new(AgentCatalogViolationCodes.SupersededProfileInvalid, "/supersedes/version", "A superseded profile is an existing, lower version of the same profile id."));
			}
		}

		return violations;
	}

	public static IReadOnlyList<AgentCatalogViolation> ValidateModifierPolicy(ModifierPolicyContent policy)
	{
		var violations = new List<AgentCatalogViolation>();
		if (policy.PerDimensionBound > MaxPerDimensionBound)
		{
			violations.Add(new(AgentCatalogViolationCodes.ModifierBoundExceeded, "/perDimensionBound", $"The per-dimension bound is at most {MaxPerDimensionBound}."));
		}

		if (policy.MaxPositiveMovement > MaxPositiveMovementLimit)
		{
			violations.Add(new(AgentCatalogViolationCodes.ModifierMovementExceeded, "/maxPositiveMovement", $"The maximum positive movement is at most {MaxPositiveMovementLimit}."));
		}

		for (var i = 0; i < policy.ContextCategories.Count; i++)
		{
			if (!PermittedContextCategories.Contains(policy.ContextCategories[i], StringComparer.Ordinal))
			{
				violations.Add(new(AgentCatalogViolationCodes.ContextCategoryNotPermitted, $"/contextCategories/{i}", $"Context category '{policy.ContextCategories[i]}' is not permitted."));
			}
		}

		return violations;
	}

	public static IReadOnlyList<AgentCatalogViolation> ValidateAgentVersion(AgentVersionContent version, AgentVersionReferences references)
	{
		var violations = new List<AgentCatalogViolation>();
		var expectedGroup = AgentCatalogIds.GroupOf(version.AgentId);
		var isCoach = string.Equals(version.Group, AgentCatalogIds.GroupCoach, StringComparison.Ordinal);

		if (expectedGroup is null)
		{
			violations.Add(new(AgentCatalogViolationCodes.UnknownAgentId, "/agentId", $"'{version.AgentId}' is not a catalog agent identifier."));
		}
		else if (!string.Equals(expectedGroup, version.Group, StringComparison.Ordinal))
		{
			violations.Add(new(AgentCatalogViolationCodes.GroupMismatch, "/group", $"Agent '{version.AgentId}' belongs to the group '{expectedGroup}'."));
		}

		if (isCoach && string.IsNullOrWhiteSpace(version.Disclosure))
		{
			violations.Add(new(AgentCatalogViolationCodes.DisclosureMissing, "/disclosure", "Coach Agent versions carry a disclosure."));
		}

		if (isCoach)
		{
			if (version.PhilosophyProfile is null)
			{
				violations.Add(new(AgentCatalogViolationCodes.ArtifactNotFound, "/artifacts/philosophyProfile", "Coach Agent versions reference a philosophy profile."));
			}

			if (version.ModifierPolicy is null)
			{
				violations.Add(new(AgentCatalogViolationCodes.ArtifactNotFound, "/artifacts/modifierPolicy", "Coach Agent versions reference a modifier policy."));
			}
		}
		else
		{
			if (version.PhilosophyProfile is not null)
			{
				violations.Add(new(AgentCatalogViolationCodes.ProfileSlotNotAllowed, "/artifacts/philosophyProfile", "Specialist Agent versions reference no philosophy profile."));
			}

			if (version.ModifierPolicy is not null)
			{
				violations.Add(new(AgentCatalogViolationCodes.ProfileSlotNotAllowed, "/artifacts/modifierPolicy", "Specialist Agent versions reference no modifier policy."));
			}
		}

		var prompt = Resolve(references.PromptTemplate, version.PromptTemplate, "/artifacts/promptTemplate", violations);
		Resolve(references.ModelSelection, version.ModelSelection, "/artifacts/modelSelection", violations);
		var tools = Resolve(references.ToolPolicy, version.ToolPolicy, "/artifacts/toolPolicy", violations);
		var safety = Resolve(references.SafetyPolicy, version.SafetyPolicy, "/artifacts/safetyPolicy", violations);

		if (prompt is not null && !string.Equals(prompt.Group, version.Group, StringComparison.Ordinal))
		{
			violations.Add(new(AgentCatalogViolationCodes.GroupMismatch, "/artifacts/promptTemplate", "The prompt template group differs from the agent version group."));
		}

		if (safety is not null && !string.Equals(safety.Group, version.Group, StringComparison.Ordinal))
		{
			violations.Add(new(AgentCatalogViolationCodes.GroupMismatch, "/artifacts/safetyPolicy", "The safety policy group differs from the agent version group."));
		}

		if (safety is not null && isCoach)
		{
			CheckSafetyRules(safety, "/artifacts/safetyPolicy", violations);
		}

		if (tools is not null)
		{
			var baseline = AgentCatalogIds.PermittedToolsOf(version.AgentId);
			for (var i = 0; i < tools.Tools.Count; i++)
			{
				var tool = tools.Tools[i];
				if (!AgentCatalogIds.IsAllowlistedTool(tool))
				{
					violations.Add(new(AgentCatalogViolationCodes.UnknownTool, $"/artifacts/toolPolicy/tools/{i}", $"Tool '{tool}' is not on the allowlist."));
				}
				else if (baseline is not null && !baseline.Contains(tool, StringComparer.Ordinal))
				{
					violations.Add(new(AgentCatalogViolationCodes.ToolOutsideBaseline, $"/artifacts/toolPolicy/tools/{i}", $"Tool '{tool}' is outside the baseline of '{version.AgentId}'."));
				}
			}
		}

		if (isCoach)
		{
			var profile = version.PhilosophyProfile is null || references.PhilosophyProfile is null
				? null
				: Resolve(references.PhilosophyProfile, version.PhilosophyProfile, "/artifacts/philosophyProfile", violations);
			var modifier = version.ModifierPolicy is null || references.ModifierPolicy is null
				? null
				: Resolve(references.ModifierPolicy, version.ModifierPolicy, "/artifacts/modifierPolicy", violations);

			if (profile is not null && references.DimensionSet?.Record is null)
			{
				violations.Add(new(AgentCatalogViolationCodes.DimensionSetNotFound, "/artifacts/philosophyProfile", $"Dimension set '{profile.DimensionSetId}' {profile.DimensionSetVersion} does not exist."));
			}

			var profileMajor = profile is null ? null : SemanticVersion.MajorOf(profile.DimensionSetVersion);
			var incompatible = (prompt?.DimensionSetMajor is { } promptMajor && profileMajor is { } pm && promptMajor != pm)
				|| (modifier is not null && profileMajor is { } mm && modifier.DimensionSetMajor != mm)
				|| (prompt?.DimensionSetMajor is { } pj && modifier is not null && modifier.DimensionSetMajor != pj);
			if (incompatible)
			{
				violations.Add(new(AgentCatalogViolationCodes.DimensionSetIncompatible, "/artifacts", "The prompt template, philosophy profile, and modifier policy must share one dimension-set major version."));
			}
		}

		return violations;
	}

	private static T? Resolve<T>(ResolvedArtifact<T> resolved, ArtifactReference reference, string pointer, List<AgentCatalogViolation> violations)
		where T : class
	{
		if (resolved.Record is null)
		{
			violations.Add(new(
				resolved.KindMismatch ? AgentCatalogViolationCodes.ArtifactKindMismatch : AgentCatalogViolationCodes.ArtifactNotFound,
				pointer,
				$"Artifact '{reference.Id}' {reference.Version} with the stated digest does not exist."));
		}

		return resolved.Record;
	}

	private static void CheckAllowlist(IReadOnlyList<string> tools, string pointer, List<AgentCatalogViolation> violations)
	{
		for (var i = 0; i < tools.Count; i++)
		{
			if (!AgentCatalogIds.IsAllowlistedTool(tools[i]))
			{
				violations.Add(new(AgentCatalogViolationCodes.UnknownTool, $"{pointer}/{i}", $"Tool '{tools[i]}' is not on the allowlist."));
			}
		}
	}

	private static void CheckSafetyRules(SafetyPolicyContent policy, string pointer, List<AgentCatalogViolation> violations)
	{
		if (!string.Equals(policy.Group, AgentCatalogIds.GroupCoach, StringComparison.Ordinal))
		{
			return;
		}

		foreach (var rule in RequiredCoachSafetyRules.Where(rule => !policy.Rules.Contains(rule, StringComparer.Ordinal)))
		{
			violations.Add(new(AgentCatalogViolationCodes.SafetyRuleMissing, pointer, $"Coach safety policies include '{rule}'."));
		}
	}

	private static string Escape(string token) => token.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);

	[GeneratedRegex(@"\{\{(?<slot>[a-z_]+)\}\}", RegexOptions.CultureInvariant)]
	private static partial Regex SlotPattern();
}
