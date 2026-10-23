using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using Shouldly;
using SocAlytics.Platform.Domain.Registry;
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

    private static string[] ErrorPointers(EvaluationResults result) =>
        (result.Details ?? []).Where(d => d.Errors is { Count: > 0 }).Select(d => d.InstanceLocation.ToString()).Distinct().ToArray();

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

    private static JsonNode Expectation(string name)
    {
        var expectations = JsonNode.Parse(File.ReadAllText(Path.Combine(VersionRoot, "examples", "invalid", "expectations.json")))!.AsObject();
        return expectations[name].ShouldNotBeNull($"{name} declares no expectation");
    }

    private static string? ExpectedCode(JsonNode expectation) =>
        expectation is JsonObject o ? o["code"]?.GetValue<string>() : null;

    private static Dictionary<string, JsonElement> PayloadSchemas(string kind, string name)
    {
        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(VersionRoot, "examples", kind, name)))!;
        return manifest["output"]!["factPayloadSchemas"]!.AsObject()
            .ToDictionary(p => p.Key, p => JsonSerializer.SerializeToElement(p.Value));
    }

    public static TheoryData<string> SchemaRefusedExamples() =>
        [.. ExampleNames("invalid").Where(n => ExpectedCode(Expectation(n)) is null)];

    public static TheoryData<string> CodeCheckedExamples() =>
        [.. ExampleNames("invalid").Where(n => ExpectedCode(Expectation(n)) is not null)];

    [Theory]
    [MemberData(nameof(SchemaRefusedExamples))]
    public void Invalid_example_fails_with_its_expected_keyword_or_pointer(string name)
    {
        var expectation = Expectation(name);
        var result = Evaluate(Path.Combine(VersionRoot, "examples", "invalid", name));

        result.IsValid.ShouldBeFalse(name);
        if (expectation is JsonObject o)
        {
            var pointer = o["pointer"]!.GetValue<string>();
            ErrorPointers(result).ShouldContain(p => p == pointer || p.StartsWith(pointer + "/", StringComparison.Ordinal), name);
        }
        else
        {
            Keywords(result).ShouldContain(expectation.GetValue<string>(), name);
        }
    }

    [Theory]
    [MemberData(nameof(CodeCheckedExamples))]
    public void Code_checked_invalid_example_gives_its_named_domain_refusal(string name)
    {
        var violation = FactPayloadSchemaLimits.Check(PayloadSchemas("invalid", name));

        violation.ShouldNotBeNull(name);
        violation.Code.ShouldBe(ExpectedCode(Expectation(name)), name);
    }

    [Theory]
    [InlineData("segment-with-payload-schemas.json")]
    [InlineData("match-with-payload-schemas.json")]
    public void Valid_payload_schema_examples_are_within_the_domain_limits(string name) =>
        FactPayloadSchemaLimits.Check(PayloadSchemas("valid", name)).ShouldBeNull(name);

    [Fact]
    public void Earlier_baselines_are_byte_identical_and_earlier_valid_manifests_stay_valid()
    {
        var releases = Path.Combine(VersionRoot, "releases");
        Sha256(Path.Combine(releases, "1.0.0.schema.json")).ShouldBe("9c4e6316936cb5209543bc066faba96b01987bf0b38e74dad31ed1e2781f299f");
        Sha256(Path.Combine(releases, "1.1.0.schema.json")).ShouldBe("a14f8b7aa24d3f22d8917445613ce3fd6e8d40e8b650978e3ff2edf5629d4088");

        foreach (var file in Directory.EnumerateFiles(Path.Combine(VersionRoot, "examples", "valid"), "*.json"))
        {
            var version = JsonNode.Parse(File.ReadAllText(file))!["manifestVersion"]!.GetValue<string>();
            var name = Path.GetFileName(file);
            Evaluate(file).IsValid.ShouldBeTrue($"{name} under the current schema");
            foreach (var release in new[] { "1.0.0", "1.1.0", "1.2.0" }.Where(r => string.CompareOrdinal(r, version) >= 0))
            {
                ContractCatalog.EvaluateAgainstRelease(Path.Combine(releases, release + ".schema.json"), file).IsValid
                    .ShouldBeTrue($"{name} ({version}) under {release}");
            }
        }
    }

    private static string Sha256(string file) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(file)));

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
        foreach (var name in new[] { "unknown-keyword", "external-ref", "cyclic-ref", "too-deep", "too-large", "root-not-object" })
        {
            invalid.ShouldContain($"payload-schema-{name}.json");
        }

        invalid.ShouldContain("payload-schemas-too-many-kinds.json");
        invalid.ShouldContain("payload-schemas-on-old-version.json");
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
