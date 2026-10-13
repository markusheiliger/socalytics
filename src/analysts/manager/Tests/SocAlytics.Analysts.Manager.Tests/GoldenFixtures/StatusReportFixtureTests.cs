using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using Shouldly;
using Xunit;

namespace SocAlytics.Analysts.Manager.Tests.GoldenFixtures;

public class StatusReportFixtureTests
{
    private static readonly string Dir =
        Path.Combine(AppContext.BaseDirectory, "contracts", "analyst-manager", "status", "v1");

    private static readonly JsonSchema Schema = JsonSchema.FromText(File.ReadAllText(Path.Combine(Dir, "status.schema.json")));

    private static bool Validates(string file) =>
        Schema.Evaluate(JsonDocument.Parse(File.ReadAllText(Path.Combine(Dir, "examples", file))).RootElement,
            new EvaluationOptions { OutputFormat = OutputFormat.List }).IsValid;

    private static bool WithinMaximum(string file)
    {
        var node = JsonNode.Parse(File.ReadAllText(Path.Combine(Dir, "examples", file)))!;
        return (int)node["activeAnalysts"]! <= (int)node["maxAnalysts"]!;
    }

    [Theory]
    [InlineData("valid/running.request.json")]
    [InlineData("valid/paused.request.json")]
    [InlineData("valid/runtime-unavailable.request.json")]
    [InlineData("valid/draining.request.json")]
    [InlineData("valid/accepted.response.json")]
    [InlineData("valid/rejected.response.json")]
    public void Valid_examples_validate(string file) => Validates(file).ShouldBeTrue(file);

    [Theory]
    [InlineData("invalid/paused-until-without-paused.request.json")]
    [InlineData("invalid/unknown-state.request.json")]
    [InlineData("invalid/reason-without-runtime-unavailable.request.json")]
    [InlineData("invalid/rejected-without-violations.response.json")]
    public void Invalid_examples_are_refused(string file) => Validates(file).ShouldBeFalse(file);

    [Fact]
    public void Active_above_maximum_violates_the_cross_field_rule()
    {
        WithinMaximum("invalid/active-above-maximum.request.json").ShouldBeFalse();
        WithinMaximum("valid/running.request.json").ShouldBeTrue();
    }
}
