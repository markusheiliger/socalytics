using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using Shouldly;
using Xunit;

namespace SocAlytics.Platform.Contracts.Tests;

public class SegmentManifestContractTests
{
    private static readonly string Directory_ = Path.Combine(AppContext.BaseDirectory, "contracts", "segments", "segment-manifest", "v1");

    private static EvaluationResults Evaluate(string file)
    {
        var schemaFile = Path.Combine(Directory_, "segment-manifest.schema.json");
        var id = JsonNode.Parse(File.ReadAllText(schemaFile))!["$id"]!.GetValue<string>();
        var schema = (JsonSchema)ContractCatalog.LoadRegistry().Get(new Uri(id))!;
        var instance = JsonDocument.Parse(File.ReadAllText(file)).RootElement;
        return schema.Evaluate(instance, new EvaluationOptions { OutputFormat = OutputFormat.List, RequireFormatValidation = true });
    }

    private static IEnumerable<string> Keywords(EvaluationResults result)
    {
        foreach (var detail in result.Details ?? [])
        {
            if (detail.Errors is null)
            {
                continue;
            }

            foreach (var key in detail.Errors.Keys)
            {
                yield return key;
            }

            foreach (var segment in detail.EvaluationPath.ToString().Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                yield return segment;
            }
        }
    }

    [Theory]
    [InlineData("segment-manifest.json")]
    [InlineData("clipped-segment-manifest.json")]
    public void Valid_examples_validate(string name)
    {
        Evaluate(Path.Combine(Directory_, "examples", "valid", name)).IsValid.ShouldBeTrue(name);
    }

    [Fact]
    public void Invalid_examples_fail_with_their_expected_keyword()
    {
        var invalid = Path.Combine(Directory_, "examples", "invalid");
        var expectations = JsonNode.Parse(File.ReadAllText(Path.Combine(invalid, "expectations.json")))!.AsObject();
        var files = System.IO.Directory.EnumerateFiles(invalid, "*.json").Where(f => Path.GetFileName(f) != "expectations.json").ToList();
        files.Count.ShouldBe(expectations.Count);
        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            var result = Evaluate(file);
            result.IsValid.ShouldBeFalse(name);
            Keywords(result).ShouldContain(expectations[name]!.GetValue<string>(), name);
        }
    }

    [Fact]
    public void Release_baseline_equals_the_current_schema()
    {
        File.ReadAllText(Path.Combine(Directory_, "releases", "1.0.0.schema.json"))
            .ShouldBe(File.ReadAllText(Path.Combine(Directory_, "segment-manifest.schema.json")));
    }
}
