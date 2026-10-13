using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using Shouldly;
using Xunit;

namespace SocAlytics.Platform.Contracts.Tests;

public class StatusReportContractTests
{
    private const string SchemaPath = "contracts/analyst-manager/status/v1/status.schema.json";

    private static readonly string Folder =
        Path.Combine(AppContext.BaseDirectory, "contracts", "analyst-manager", "status", "v1", "examples");

    private static bool Validates(string file)
    {
        var schema = (JsonSchema)ContractCatalog.LoadRegistry().Get(new Uri("https://socalytics.invalid/" + SchemaPath))!;
        var instance = JsonDocument.Parse(File.ReadAllText(Path.Combine(Folder, file))).RootElement;
        return schema.Evaluate(instance, new EvaluationOptions { OutputFormat = OutputFormat.List }).IsValid;
    }

    private static bool WithinMaximum(string file)
    {
        var node = JsonNode.Parse(File.ReadAllText(Path.Combine(Folder, file)))!;
        return node["activeAnalysts"]!.GetValue<int>() <= node["maxAnalysts"]!.GetValue<int>();
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
    public void Active_above_maximum_is_schema_valid_but_violates_the_cross_field_rule()
    {
        const string file = "invalid/active-above-maximum.request.json";
        Validates(file).ShouldBeTrue(file);
        WithinMaximum(file).ShouldBeFalse(file);
    }

    [Fact]
    public void Valid_requests_stay_within_the_maximum()
    {
        foreach (var file in Directory.EnumerateFiles(Path.Combine(Folder, "valid"), "*.request.json"))
        {
            WithinMaximum(Path.GetRelativePath(Folder, file)).ShouldBeTrue(file);
        }
    }

    [Fact]
    public void Every_example_file_is_covered_by_a_test_case()
    {
        Directory.EnumerateFiles(Folder, "*.json", SearchOption.AllDirectories).Count().ShouldBe(11);
    }
}
