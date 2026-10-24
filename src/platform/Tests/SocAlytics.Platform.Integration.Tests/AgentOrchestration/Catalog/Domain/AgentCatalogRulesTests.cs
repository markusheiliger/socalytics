using Shouldly;
using SocAlytics.Platform.Domain.AgentOrchestration;
using SocAlytics.Platform.Domain.AgentOrchestration.Catalog;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.AgentOrchestration.Catalog.Domain;

public sealed class AgentCatalogRulesTests
{
	private const string Digest = "sha256:0000000000000000000000000000000000000000000000000000000000000000";
	private static readonly string[] CoachSafetyRules = ["simulation-disclosure", "non-impersonation", "non-fabrication"];

	private static ArtifactReference Ref(string id, string version = "1.0.0") => new(id, version, Digest);

	private static ResolvedArtifact<T> Found<T>(T record) where T : class => new(record);

	private static string[] Codes(IReadOnlyList<AgentCatalogViolation> violations) =>
		[.. violations.Select(violation => violation.Code).Distinct()];

	private static PromptTemplateContent CoachPrompt(string? text = null, string[]? slots = null, int? major = 1) => new(
		"coach-prompt",
		"1.0.0",
		AgentCatalogIds.GroupCoach,
		text ?? "{{agent_id}} {{display_name}} {{philosophy_profile}} {{tool_policy}} {{evidence_context}}",
		slots ?? ["agent_id", "display_name", "philosophy_profile", "tool_policy", "evidence_context"],
		major);

	private static PromptTemplateContent SpecialistPrompt(string? text = null, string[]? slots = null) => new(
		"specialist-prompt",
		"1.0.0",
		AgentCatalogIds.GroupSpecialist,
		text ?? "{{agent_id}} {{display_name}} {{tool_policy}} {{evidence_context}}",
		slots ?? ["agent_id", "display_name", "tool_policy", "evidence_context"],
		null);

	private static DimensionSetContent Dimensions(params string[] ids) =>
		new("coach-philosophy-dimensions", "1.0.0", [.. ids.Select(id => new DimensionContent(id, ["get_match"]))]);

	private static Dictionary<string, ProfileWeightContent> Weights(params (string Id, int Weight)[] weights) =>
		weights.ToDictionary(weight => weight.Id, weight => new ProfileWeightContent(weight.Weight, "rationale", "provenance"), StringComparer.Ordinal);

	private static PhilosophyProfileContent Profile(
		Dictionary<string, ProfileWeightContent> weights,
		string version = "1.0.0",
		string? supersedes = null) =>
		new("coach-jurgen-klopp", version, "coach-philosophy-dimensions", "1.0.0", weights, supersedes);

	private static ModifierPolicyContent Modifier(int major = 1, int bound = 3, int movement = 10, string[]? categories = null) =>
		new(major, bound, movement, categories ?? ["score-state"]);

	private static AgentVersionContent CoachVersion(string agentId = AgentCatalogIds.CoachJurgenKlopp, string? disclosure = "Simulated coach.") => new(
		agentId,
		AgentCatalogIds.GroupCoach,
		disclosure,
		Ref("coach-prompt"),
		Ref("model"),
		Ref("tools"),
		Ref("safety"),
		Ref("profile"),
		Ref("modifier"));

	private static AgentVersionReferences CoachReferences(
		PromptTemplateContent? prompt = null,
		ToolPolicyContent? tools = null,
		SafetyPolicyContent? safety = null,
		ModifierPolicyContent? modifier = null,
		PhilosophyProfileContent? profile = null) => new(
		Found(prompt ?? CoachPrompt()),
		Found(new ModelSelectionContent("simulated")),
		Found(tools ?? new ToolPolicyContent(["get_match"])),
		Found(safety ?? new SafetyPolicyContent(AgentCatalogIds.GroupCoach, CoachSafetyRules)),
		Found(profile ?? Profile(Weights(("a", 100)))),
		Found(Dimensions("a")),
		Found(modifier ?? Modifier()));

	private static AgentVersionContent SpecialistVersion(string agentId = AgentCatalogIds.SpecialistFitness) => new(
		agentId,
		AgentCatalogIds.GroupSpecialist,
		null,
		Ref("specialist-prompt"),
		Ref("model"),
		Ref("tools"),
		Ref("safety"),
		null,
		null);

	private static AgentVersionReferences SpecialistReferences(ToolPolicyContent? tools = null) => new(
		Found(SpecialistPrompt()),
		Found(new ModelSelectionContent("simulated")),
		Found(tools ?? new ToolPolicyContent(["get_match"])),
		Found(new SafetyPolicyContent(AgentCatalogIds.GroupSpecialist, [])),
		null,
		null,
		null);

	[Fact]
	public void Model_selection_accepts_only_permitted_providers()
	{
		foreach (var provider in AgentCatalogRules.PermittedProviders)
		{
			AgentCatalogRules.ValidateModelSelection(new(provider)).ShouldBeEmpty();
		}

		Codes(AgentCatalogRules.ValidateModelSelection(new("anthropic"))).ShouldBe([AgentCatalogViolationCodes.ProviderNotPermitted]);
	}

	[Fact]
	public void Tool_policy_rejects_tools_outside_the_allowlist()
	{
		AgentCatalogRules.ValidateToolPolicy(new(AgentCatalogIds.ToolAllowlist)).ShouldBeEmpty();

		var violations = AgentCatalogRules.ValidateToolPolicy(new(["get_match", "delete_match"]));
		Codes(violations).ShouldBe([AgentCatalogViolationCodes.UnknownTool]);
		violations.Single().Pointer.ShouldBe("/tools/1");
	}

	[Fact]
	public void Coach_safety_policy_requires_the_three_rules_and_specialist_policy_none()
	{
		AgentCatalogRules.ValidateSafetyPolicy(new(AgentCatalogIds.GroupCoach, CoachSafetyRules)).ShouldBeEmpty();
		AgentCatalogRules.ValidateSafetyPolicy(new(AgentCatalogIds.GroupSpecialist, [])).ShouldBeEmpty();

		var violations = AgentCatalogRules.ValidateSafetyPolicy(new(AgentCatalogIds.GroupCoach, ["simulation-disclosure"]));
		Codes(violations).ShouldBe([AgentCatalogViolationCodes.SafetyRuleMissing]);
		violations.Count.ShouldBe(2);
	}

	[Fact]
	public void Prompt_template_slots_are_declared_once_and_the_profile_slot_is_coach_only()
	{
		AgentCatalogRules.ValidatePromptTemplate(CoachPrompt()).ShouldBeEmpty();
		AgentCatalogRules.ValidatePromptTemplate(SpecialistPrompt()).ShouldBeEmpty();

		Codes(AgentCatalogRules.ValidatePromptTemplate(CoachPrompt(text: "{{agent_id}} {{display_name}} {{tool_policy}} {{evidence_context}}")))
			.ShouldBe([AgentCatalogViolationCodes.ProfileSlotMissing]);
		Codes(AgentCatalogRules.ValidatePromptTemplate(CoachPrompt(text: "{{agent_id}} {{agent_id}} {{display_name}} {{philosophy_profile}} {{tool_policy}} {{evidence_context}}")))
			.ShouldBe([AgentCatalogViolationCodes.ProfileSlotMissing]);
		Codes(AgentCatalogRules.ValidatePromptTemplate(CoachPrompt(slots: ["agent_id", "display_name", "tool_policy", "evidence_context"])))
			.ShouldContain(AgentCatalogViolationCodes.ProfileSlotMissing);
		Codes(AgentCatalogRules.ValidatePromptTemplate(SpecialistPrompt(text: "{{agent_id}} {{display_name}} {{tool_policy}} {{evidence_context}} {{unknown_slot}}")))
			.ShouldBe([AgentCatalogViolationCodes.ProfileSlotNotAllowed]);
		Codes(AgentCatalogRules.ValidatePromptTemplate(SpecialistPrompt(
			text: "{{agent_id}} {{display_name}} {{philosophy_profile}} {{tool_policy}} {{evidence_context}}",
			slots: ["agent_id", "display_name", "philosophy_profile", "tool_policy", "evidence_context"])))
			.ShouldBe([AgentCatalogViolationCodes.ProfileSlotNotAllowed]);
	}

	[Fact]
	public void Dimension_set_checks_unique_ids_allowlisted_evidence_and_stable_ids_within_a_major()
	{
		AgentCatalogRules.ValidateDimensionSet(Dimensions("a", "b"), []).ShouldBeEmpty();
		AgentCatalogRules.ValidateDimensionSet(Dimensions("a", "b"), [Dimensions("b", "a")]).ShouldBeEmpty();

		Codes(AgentCatalogRules.ValidateDimensionSet(Dimensions("a", "a"), [])).ShouldBe([AgentCatalogViolationCodes.SchemaViolation]);
		Codes(AgentCatalogRules.ValidateDimensionSet(
			new("coach-philosophy-dimensions", "1.1.0", [new("a", ["get_match()"])]), []))
			.ShouldBe([AgentCatalogViolationCodes.UnknownTool]);
		Codes(AgentCatalogRules.ValidateDimensionSet(Dimensions("a", "c"), [Dimensions("a", "b")]))
			.ShouldBe([AgentCatalogViolationCodes.DimensionSetIdsChanged]);
	}

	[Fact]
	public void Profile_requires_every_dimension_a_weight_total_of_100_and_documented_weights()
	{
		var set = Found(Dimensions("a", "b"));

		AgentCatalogRules.ValidateProfile(Profile(Weights(("a", 60), ("b", 40))), set, []).ShouldBeEmpty();

		Codes(AgentCatalogRules.ValidateProfile(Profile(Weights(("a", 100))), set, [])).ShouldBe([AgentCatalogViolationCodes.MissingDimension]);
		Codes(AgentCatalogRules.ValidateProfile(Profile(Weights(("a", 50), ("b", 30), ("c", 20))), set, [])).ShouldBe([AgentCatalogViolationCodes.UnknownDimension]);
		Codes(AgentCatalogRules.ValidateProfile(Profile(Weights(("a", 60), ("b", 30))), set, [])).ShouldBe([AgentCatalogViolationCodes.WeightsTotalNot100]);
		Codes(AgentCatalogRules.ValidateProfile(Profile(Weights(("a", 101), ("b", -1))), set, []))
			.ShouldBe([AgentCatalogViolationCodes.WeightOutOfRange]);

		var undocumented = new Dictionary<string, ProfileWeightContent>(StringComparer.Ordinal)
		{
			["a"] = new(60, " ", "provenance"),
			["b"] = new(40, "rationale", ""),
		};
		var violations = AgentCatalogRules.ValidateProfile(Profile(undocumented), set, []);
		Codes(violations).ShouldBe([AgentCatalogViolationCodes.RationaleMissing]);
		violations.Count.ShouldBe(2);
	}

	[Fact]
	public void Profile_reports_a_missing_or_wrong_kind_dimension_set()
	{
		var weights = Weights(("a", 100));

		Codes(AgentCatalogRules.ValidateProfile(Profile(weights), new(null), [])).ShouldBe([AgentCatalogViolationCodes.DimensionSetNotFound]);
		Codes(AgentCatalogRules.ValidateProfile(Profile(weights), new(null, KindMismatch: true), [])).ShouldBe([AgentCatalogViolationCodes.ArtifactKindMismatch]);
	}

	[Theory]
	[InlineData("1.0.0", "0.9.0", true, true)]
	[InlineData("1.0.0", "0.9.0", false, false)]
	[InlineData("1.0.0", "1.0.0", true, false)]
	[InlineData("1.0.0", "1.1.0", true, false)]
	[InlineData("1.0.0", "not-a-version", true, false)]
	public void Superseded_profile_is_an_existing_lower_version(string version, string supersedes, bool exists, bool valid)
	{
		var violations = AgentCatalogRules.ValidateProfile(
			Profile(Weights(("a", 100)), version, supersedes),
			Found(Dimensions("a")),
			exists ? [supersedes] : []);

		if (valid)
		{
			violations.ShouldBeEmpty();
		}
		else
		{
			Codes(violations).ShouldBe([AgentCatalogViolationCodes.SupersededProfileInvalid]);
		}
	}

	[Fact]
	public void Modifier_policy_is_bounded_and_limited_to_permitted_context_categories()
	{
		AgentCatalogRules.ValidateModifierPolicy(Modifier()).ShouldBeEmpty();
		AgentCatalogRules.ValidateModifierPolicy(Modifier(categories: [.. AgentCatalogRules.PermittedContextCategories])).ShouldBeEmpty();

		Codes(AgentCatalogRules.ValidateModifierPolicy(Modifier(bound: 4))).ShouldBe([AgentCatalogViolationCodes.ModifierBoundExceeded]);
		Codes(AgentCatalogRules.ValidateModifierPolicy(Modifier(movement: 11))).ShouldBe([AgentCatalogViolationCodes.ModifierMovementExceeded]);
		Codes(AgentCatalogRules.ValidateModifierPolicy(Modifier(categories: ["weather"]))).ShouldBe([AgentCatalogViolationCodes.ContextCategoryNotPermitted]);
	}

	[Fact]
	public void Valid_coach_and_specialist_versions_have_no_violations()
	{
		AgentCatalogRules.ValidateAgentVersion(CoachVersion(), CoachReferences()).ShouldBeEmpty();
		AgentCatalogRules.ValidateAgentVersion(SpecialistVersion(), SpecialistReferences()).ShouldBeEmpty();
	}

	[Fact]
	public void Agent_version_must_name_a_catalog_agent_of_its_group()
	{
		Codes(AgentCatalogRules.ValidateAgentVersion(CoachVersion("coach-someone-else"), CoachReferences()))
			.ShouldBe([AgentCatalogViolationCodes.UnknownAgentId]);
		Codes(AgentCatalogRules.ValidateAgentVersion(CoachVersion(AgentCatalogIds.SpecialistFitness), CoachReferences()))
			.ShouldContain(AgentCatalogViolationCodes.GroupMismatch);
	}

	[Fact]
	public void Coach_version_needs_a_disclosure_a_profile_and_a_modifier_policy()
	{
		Codes(AgentCatalogRules.ValidateAgentVersion(CoachVersion(disclosure: " "), CoachReferences()))
			.ShouldBe([AgentCatalogViolationCodes.DisclosureMissing]);

		var version = CoachVersion() with { PhilosophyProfile = null, ModifierPolicy = null };
		var violations = AgentCatalogRules.ValidateAgentVersion(version, CoachReferences() with { PhilosophyProfile = null, DimensionSet = null, ModifierPolicy = null });
		Codes(violations).ShouldBe([AgentCatalogViolationCodes.ArtifactNotFound]);
		violations.Count.ShouldBe(2);
	}

	[Fact]
	public void Specialist_version_references_no_profile_or_modifier_policy()
	{
		var version = SpecialistVersion() with { PhilosophyProfile = Ref("profile"), ModifierPolicy = Ref("modifier") };

		var violations = AgentCatalogRules.ValidateAgentVersion(version, SpecialistReferences());
		Codes(violations).ShouldBe([AgentCatalogViolationCodes.ProfileSlotNotAllowed]);
		violations.Count.ShouldBe(2);
	}

	[Fact]
	public void Agent_version_reports_missing_and_wrong_kind_artifacts()
	{
		var missing = SpecialistReferences() with { ModelSelection = new(null), ToolPolicy = new(null, KindMismatch: true) };

		var violations = AgentCatalogRules.ValidateAgentVersion(SpecialistVersion(), missing);

		Codes(violations).ShouldBe([AgentCatalogViolationCodes.ArtifactNotFound, AgentCatalogViolationCodes.ArtifactKindMismatch]);
	}

	[Fact]
	public void Agent_version_artifacts_must_share_the_group()
	{
		var references = SpecialistReferences() with
		{
			PromptTemplate = Found(CoachPrompt()),
			SafetyPolicy = Found(new SafetyPolicyContent(AgentCatalogIds.GroupCoach, CoachSafetyRules)),
		};

		var violations = AgentCatalogRules.ValidateAgentVersion(SpecialistVersion(), references);

		Codes(violations).ShouldBe([AgentCatalogViolationCodes.GroupMismatch]);
		violations.Count.ShouldBe(2);
	}

	[Fact]
	public void Coach_agent_version_rechecks_the_safety_rules()
	{
		var references = CoachReferences(safety: new SafetyPolicyContent(AgentCatalogIds.GroupCoach, ["simulation-disclosure"]));

		Codes(AgentCatalogRules.ValidateAgentVersion(CoachVersion(), references)).ShouldBe([AgentCatalogViolationCodes.SafetyRuleMissing]);
	}

	[Fact]
	public void Agent_version_tools_stay_on_the_allowlist_and_inside_the_agent_baseline()
	{
		Codes(AgentCatalogRules.ValidateAgentVersion(SpecialistVersion(), SpecialistReferences(new(["get_match", "delete_match"]))))
			.ShouldBe([AgentCatalogViolationCodes.UnknownTool]);

		// get_heatmap is on the allowlist but not in the Fitness Specialist baseline.
		Codes(AgentCatalogRules.ValidateAgentVersion(SpecialistVersion(), SpecialistReferences(new(["get_heatmap"]))))
			.ShouldBe([AgentCatalogViolationCodes.ToolOutsideBaseline]);
		Codes(AgentCatalogRules.ValidateAgentVersion(
			SpecialistVersion(AgentCatalogIds.SpecialistVideoAnalysis),
			SpecialistReferences(new(["get_player_stats"]))))
			.ShouldBe([AgentCatalogViolationCodes.ToolOutsideBaseline]);
	}

	[Fact]
	public void Coach_agent_version_needs_an_existing_dimension_set_of_the_profile()
	{
		var references = CoachReferences() with { DimensionSet = new(null) };

		Codes(AgentCatalogRules.ValidateAgentVersion(CoachVersion(), references)).ShouldBe([AgentCatalogViolationCodes.DimensionSetNotFound]);
	}

	[Fact]
	public void Coach_agent_version_artifacts_share_one_dimension_set_major()
	{
		Codes(AgentCatalogRules.ValidateAgentVersion(CoachVersion(), CoachReferences(prompt: CoachPrompt(major: 2))))
			.ShouldBe([AgentCatalogViolationCodes.DimensionSetIncompatible]);
		Codes(AgentCatalogRules.ValidateAgentVersion(CoachVersion(), CoachReferences(modifier: Modifier(major: 2))))
			.ShouldBe([AgentCatalogViolationCodes.DimensionSetIncompatible]);

		var profileOfMajorTwo = new PhilosophyProfileContent(
			"coach-jurgen-klopp", "1.0.0", "coach-philosophy-dimensions", "2.0.0", Weights(("a", 100)), null);
		Codes(AgentCatalogRules.ValidateAgentVersion(CoachVersion(), CoachReferences(profile: profileOfMajorTwo)))
			.ShouldBe([AgentCatalogViolationCodes.DimensionSetIncompatible]);
	}

	[Theory]
	[InlineData("0.0.0")]
	[InlineData("1.2.3")]
	[InlineData("10.20.30-alpha.1")]
	[InlineData("1.0.0-0.3.7")]
	[InlineData("1.0.0-x-y-z.--")]
	public void Semantic_version_parses_valid_versions(string value)
	{
		SemanticVersion.TryParse(value, out var version).ShouldBeTrue();
		version.ToString().ShouldBe(value);
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("1")]
	[InlineData("1.0")]
	[InlineData("01.0.0")]
	[InlineData("1.0.0.0")]
	[InlineData("v1.0.0")]
	[InlineData("1.0.0+build")]
	[InlineData("1.0.0-")]
	[InlineData("1.0.0-01")]
	[InlineData("1.0.0-a..b")]
	[InlineData("99999999999.0.0")]
	public void Semantic_version_rejects_invalid_versions(string? value)
	{
		SemanticVersion.TryParse(value, out _).ShouldBeFalse();
	}

	[Fact]
	public void Semantic_version_rejects_overlong_values()
	{
		SemanticVersion.TryParse("1.0.0-" + new string('a', 130), out _).ShouldBeFalse();
	}

	[Fact]
	public void Semantic_version_orders_by_the_semver_precedence_rules()
	{
		string[] ascending =
		[
			"1.0.0-alpha", "1.0.0-alpha.1", "1.0.0-alpha.beta", "1.0.0-beta", "1.0.0-beta.2", "1.0.0-beta.11", "1.0.0-rc.1",
			"1.0.0", "1.0.1", "1.1.0", "2.0.0", "10.0.0",
		];
		var versions = ascending.Select(value => SemanticVersion.TryParse(value, out var version) ? version : throw new InvalidOperationException(value)).ToList();

		for (var i = 0; i < versions.Count; i++)
		{
			for (var j = 0; j < versions.Count; j++)
			{
				Math.Sign(versions[i].CompareTo(versions[j])).ShouldBe(Math.Sign(i.CompareTo(j)), $"{ascending[i]} vs {ascending[j]}");
			}
		}

		versions[0].CompareTo(null).ShouldBe(1);
		versions[7].IsPreRelease.ShouldBeFalse();
		versions[0].IsPreRelease.ShouldBeTrue();
		(versions[7] > versions[6]).ShouldBeTrue();
		(versions[6] < versions[7]).ShouldBeTrue();
		(versions[7] >= versions[7]).ShouldBeTrue();
		(versions[7] <= versions[7]).ShouldBeTrue();
	}

	[Fact]
	public void Semantic_version_equality_follows_precedence()
	{
		SemanticVersion.TryParse("1.2.3-rc.1", out var first).ShouldBeTrue();
		SemanticVersion.TryParse("1.2.3-rc.1", out var second).ShouldBeTrue();
		SemanticVersion.TryParse("1.2.3", out var release).ShouldBeTrue();

		first.Equals(second).ShouldBeTrue();
		first.GetHashCode().ShouldBe(second.GetHashCode());
		first.Equals(release).ShouldBeFalse();
		first.Equals((object)second).ShouldBeTrue();
		SemanticVersion.MajorOf("3.1.4").ShouldBe(3);
		SemanticVersion.MajorOf("junk").ShouldBeNull();
	}

	[Fact]
	public void Profile_distance_is_the_sum_of_absolute_differences_over_200()
	{
		var first = new Dictionary<string, int> { ["a"] = 60, ["b"] = 40 };
		var second = new Dictionary<string, int> { ["a"] = 40, ["c"] = 60 };

		ProfileDistance.Between(first, first).ShouldBe(0.0);
		// |60-40| + |40-0| + |0-60| = 120
		ProfileDistance.Between(first, second).ShouldBe(0.6);
		ProfileDistance.Between(second, first).ShouldBe(0.6);
		ProfileDistance.RequiresReview(0.09).ShouldBeTrue();
		ProfileDistance.RequiresReview(ProfileDistance.ReviewThreshold).ShouldBeFalse();
	}
}
