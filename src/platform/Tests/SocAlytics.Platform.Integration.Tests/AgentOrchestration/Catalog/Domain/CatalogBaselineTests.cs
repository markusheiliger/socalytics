using System.Text.Json;
using Shouldly;
using SocAlytics.Platform.Domain.AgentOrchestration;
using SocAlytics.Platform.Domain.AgentOrchestration.Catalog;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.AgentOrchestration.Catalog.Domain;

public sealed class CatalogBaselineTests
{
	private static string ContractsRoot()
	{
		var dir = new DirectoryInfo(AppContext.BaseDirectory);
		while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "contracts", "agent-catalog")))
		{
			dir = dir.Parent;
		}

		return Path.Combine(dir?.FullName ?? throw new DirectoryNotFoundException("contracts/agent-catalog"), "contracts", "agent-catalog");
	}

	private static JsonElement Load(params string[] relative) =>
		JsonDocument.Parse(File.ReadAllText(Path.Combine([ContractsRoot(), .. relative]))).RootElement;

	private static string[] Strings(JsonElement array) => [.. array.EnumerateArray().Select(item => item.GetString()!)];

	[Fact]
	public void Domain_baseline_equals_the_baseline_contract_example()
	{
		var baseline = Load("catalog-baseline", "v1", "examples", "valid", "baseline-1.0.0.json");

		baseline.GetProperty("version").GetString().ShouldBe(AgentCatalogIds.BaselineVersion);
		Strings(baseline.GetProperty("toolAllowlist")).ShouldBe(AgentCatalogIds.ToolAllowlist);

		var agents = baseline.GetProperty("agents").EnumerateArray().ToList();
		agents.Select(agent => agent.GetProperty("agentId").GetString()).ShouldBe(AgentCatalogIds.All);

		foreach (var agent in agents)
		{
			var id = agent.GetProperty("agentId").GetString()!;
			agent.GetProperty("group").GetString().ShouldBe(AgentCatalogIds.GroupOf(id), id);
			agent.GetProperty("domain").GetString().ShouldBe(AgentCatalogIds.DomainOf(id), id);
			agent.GetProperty("displayName").GetString().ShouldBe(AgentCatalogIds.DisplayNames[id], id);
			Strings(agent.GetProperty("permittedTools")).ShouldBe(AgentCatalogIds.PermittedToolsOf(id)!, id);
		}
	}

	[Fact]
	public void Provisional_profiles_keep_their_documented_distances()
	{
		var klopp = Weights("coach-jurgen-klopp-1.0.0.json");
		var guardiola = Weights("coach-pep-guardiola-1.0.0.json");
		var mourinho = Weights("coach-jose-mourinho-1.0.0.json");

		foreach (var weights in new[] { klopp, guardiola, mourinho })
		{
			weights.Values.Sum().ShouldBe(100);
		}

		Math.Round(ProfileDistance.Between(klopp, guardiola), 2).ShouldBe(0.25);
		Math.Round(ProfileDistance.Between(klopp, mourinho), 2).ShouldBe(0.25);
		Math.Round(ProfileDistance.Between(guardiola, mourinho), 2).ShouldBe(0.31);

		ProfileDistance.RequiresReview(ProfileDistance.Between(klopp, guardiola)).ShouldBeFalse();
		ProfileDistance.RequiresReview(ProfileDistance.Between(klopp, mourinho)).ShouldBeFalse();
		ProfileDistance.RequiresReview(ProfileDistance.Between(guardiola, mourinho)).ShouldBeFalse();
	}

	private static Dictionary<string, int> Weights(string file) =>
		Load("philosophy-profile", "v1", "examples", "valid", file)
			.GetProperty("weights")
			.EnumerateObject()
			.ToDictionary(weight => weight.Name, weight => weight.Value.GetProperty("weight").GetInt32(), StringComparer.Ordinal);
}
