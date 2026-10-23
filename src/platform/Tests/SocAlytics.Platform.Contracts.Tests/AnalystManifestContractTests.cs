using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using Shouldly;
using Xunit;

namespace SocAlytics.Platform.Contracts.Tests;

public class AnalystManifestContractTests
{
    private static readonly string VersionRoot = Path.Combine(AppContext.BaseDirectory, "contracts", "analysis", "analyst-manifest", "v1");

    private static readonly string SchemaFile = Path.Combine(VersionRoot, "analyst-manifest.schema.json");

    private static EvaluationResults Evaluate(string file)
    {
        var id = JsonNode.Parse(File.ReadAllText(SchemaFile))!["$id"]!.GetValue<string>();
        var schema = (JsonSchema)ContractCatalog.LoadRegistry().Get(new Uri(id))!;
        var instance = JsonDocument.Parse(File.ReadAllText(file)).RootElement;
        return schema.Evaluate(instance, new EvaluationOptions { OutputFormat = OutputFormat.List });
    }

    private static string[] Keywords(EvaluationResults result)
    {
        var keywords = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var detail in result.Details ?? [])
        {
            if (detail.Errors is null)
            {
                continue;
            }

            foreach (var key in detail.Errors.Keys)
            {
                keywords.Add(key);
            }

            foreach (var segment in detail.EvaluationPath.ToString().Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                keywords.Add(segment);
            }
        }

        return keywords.ToArray();
    }

    public static TheoryData<string> ValidExamples() => [.. ExampleNames("valid")];

    public static TheoryData<string> InvalidExamples() => [.. ExampleNames("invalid")];

    private static List<string> ExampleNames(string kind) =>
        Directory.EnumerateFiles(Path.Combine(VersionRoot, "examples", kind), "*.json")
            .Select(f => Path.GetFileName(f))
            .Where(n => n != "expectations.json")
            .Order(StringComparer.Ordinal)
            .ToList();

    [Theory]
    [MemberData(nameof(ValidExamples))]
    public void Valid_example_passes(string name) =>
        Evaluate(Path.Combine(VersionRoot, "examples", "valid", name)).IsValid.ShouldBeTrue(name);

    [Theory]
    [MemberData(nameof(InvalidExamples))]
    public void Invalid_example_fails_with_its_expected_keyword(string name)
    {
        var expectations = JsonNode.Parse(File.ReadAllText(Path.Combine(VersionRoot, "examples", "invalid", "expectations.json")))!.AsObject();
        var keyword = expectations[name]?.GetValue<string>();
        keyword.ShouldNotBeNull($"{name} declares no expected keyword");

        var result = Evaluate(Path.Combine(VersionRoot, "examples", "invalid", name));

        result.IsValid.ShouldBeFalse(name);
        Keywords(result).ShouldContain(keyword, name);
    }

    [Fact]
    public void Corpus_covers_both_scopes_and_the_required_refusals()
    {
        ExampleNames("valid").Count.ShouldBeGreaterThanOrEqualTo(2);

        var invalid = ExampleNames("invalid");
        invalid.ShouldContain("missing-capability.json");
        invalid.ShouldContain("unknown-property.json");
        invalid.ShouldContain("bad-digest.json");
        invalid.ShouldContain("unknown-tier.json");
        invalid.ShouldContain("partial-coverage-zero-fraction.json");
        invalid.ShouldContain("partial-coverage-unknown-mode.json");
    }

    [Fact]
    public void Valid_examples_cover_segment_low_level_and_match_high_level_with_upstream()
    {
        var manifests = Directory.EnumerateFiles(Path.Combine(VersionRoot, "examples", "valid"), "*.json")
            .Select(f => JsonNode.Parse(File.ReadAllText(f))!)
            .ToList();

        manifests.ShouldContain(m => m["capability"]!["tier"]!.GetValue<string>() == "low-level" && m["capability"]!["executionScope"]!.GetValue<string>() == "segment");
        manifests.ShouldContain(m => m["capability"]!["tier"]!.GetValue<string>() == "high-level" && m["capability"]!["executionScope"]!.GetValue<string>() == "match" && m["upstream"]!.AsArray().Count > 0);
    }

    [Fact]
    public void Schema_refuses_unknown_properties_at_every_level_and_has_a_released_copy()
    {
        var schema = JsonNode.Parse(File.ReadAllText(SchemaFile))!;
        schema["additionalProperties"]!.GetValue<bool>().ShouldBeFalse();
        schema["x-socalytics-version"]!.GetValue<string>().ShouldBe("1.2.0");
        foreach (var path in new[] { "capability", "output", "model", "runtime", "requirements", "determinism" })
        {
            schema["properties"]![path]!["additionalProperties"]!.GetValue<bool>().ShouldBeFalse(path);
        }

        File.ReadAllBytes(Path.Combine(VersionRoot, "releases", "1.2.0.schema.json")).ShouldBe(File.ReadAllBytes(SchemaFile));
    }
}
