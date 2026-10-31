using System.Text.Json;
using Shouldly;
using SocAlytics.Platform.Domain.AgentOrchestration.Tools;
using Xunit;

namespace SocAlytics.Platform.Contracts.Tests;

public class AgentToolAllowlistTests
{
    private static JsonElement Load(params string[] segments) =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine([AppContext.BaseDirectory, "contracts", .. segments]))).RootElement;

    private static JsonElement Allowlist() =>
        Load("agent-tools", "tool-allowlist", "v1", "examples", "valid", "allowlist-1.0.0.json");

    [Fact]
    public void Version_equals_the_fixture()
    {
        Allowlist().GetProperty("allowlistVersion").GetString().ShouldBe(AgentToolAllowlist.Version);
    }

    [Fact]
    public void Entries_equal_the_fixture_in_order()
    {
        var tools = Allowlist().GetProperty("tools").EnumerateArray().ToList();

        tools.Count.ShouldBe(AgentToolAllowlist.Entries.Count);
        for (var i = 0; i < tools.Count; i++)
        {
            var fixture = tools[i];
            var entry = AgentToolAllowlist.Entries[i];

            fixture.GetProperty("toolId").GetString().ShouldBe(entry.ToolId);
            fixture.GetProperty("operationId").GetString().ShouldBe(entry.OperationId);
            fixture.GetProperty("method").GetString().ShouldBe("POST");
            fixture.GetProperty("pathTemplate").GetString().ShouldBe(entry.PathTemplate);
            fixture.GetProperty("answers").GetString().ShouldBe(entry.AnswersWireName);
            fixture.GetProperty("producingCapability").GetString().ShouldBe(entry.ProducingCapability);
        }
    }

    [Fact]
    public void Tool_identifiers_equal_the_catalog_baseline_allowlist()
    {
        var baseline = Load("agent-catalog", "catalog-baseline", "v1", "examples", "valid", "baseline-1.0.0.json");
        var baselineTools = baseline.GetProperty("toolAllowlist").EnumerateArray().Select(t => t.GetString()!).ToList();

        AgentToolAllowlist.Entries.Select(e => e.ToolId).ShouldBe(baselineTools, ignoreOrder: true);
        AgentToolAllowlist.Entries.Select(e => e.ToolId).Distinct().Count().ShouldBe(AgentToolAllowlist.Entries.Count);
    }
}
