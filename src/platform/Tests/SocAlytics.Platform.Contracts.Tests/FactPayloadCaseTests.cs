using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using Shouldly;
using SocAlytics.Platform.Domain.Registry;
using Xunit;

namespace SocAlytics.Platform.Contracts.Tests;

public class FactPayloadCaseTests
{
    private static readonly string ExamplesRoot = Path.Combine(
        AppContext.BaseDirectory, "contracts", "analysis", "analyst-manifest", "v1", "examples");

    private static JsonArray Cases() =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(ExamplesRoot, "fact-payloads.json")))!.AsArray();

    private static Dictionary<string, JsonElement> PayloadSchemas(string manifestPath)
    {
        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(ExamplesRoot, manifestPath)))!;
        return manifest["output"]!["factPayloadSchemas"]!.AsObject()
            .ToDictionary(p => p.Key, p => JsonSerializer.SerializeToElement(p.Value));
    }

    public static TheoryData<int> CaseIndexes() => [.. Enumerable.Range(0, Cases().Count)];

    public static TheoryData<string> CodeCheckedInvalidExamples()
    {
        var expectations = JsonNode.Parse(File.ReadAllText(Path.Combine(ExamplesRoot, "invalid", "expectations.json")))!.AsObject();
        return [.. expectations
            .Where(e => e.Value is JsonObject o && o["code"] is not null)
            .Select(e => e.Key)
            .Order(StringComparer.Ordinal)];
    }

    [Fact]
    public void Cases_cover_valid_and_invalid_verdicts_of_both_manifests()
    {
        var cases = Cases();
        cases.Count.ShouldBeGreaterThan(0);
        cases.ShouldContain(c => c!["valid"]!.GetValue<bool>());
        cases.ShouldContain(c => !c!["valid"]!.GetValue<bool>());
        cases.Select(c => c!["manifest"]!.GetValue<string>()).Distinct().Count().ShouldBeGreaterThanOrEqualTo(2);
    }

    [Theory]
    [MemberData(nameof(CaseIndexes))]
    public void Case_gives_its_verdict_and_first_violation_pointer(int index)
    {
        var testCase = Cases()[index]!;
        var manifest = testCase["manifest"]!.GetValue<string>();
        var kind = testCase["kind"]!.GetValue<string>();
        var expectedValid = testCase["valid"]!.GetValue<bool>();
        var at = testCase["at"]?.GetValue<string>();
        var label = $"{manifest} {kind} #{index}";

        var schemas = PayloadSchemas(manifest);
        FactPayloadSchemaLimits.Check(schemas).ShouldBeNull(label);
        schemas.ContainsKey(kind).ShouldBeTrue(label);

        var schema = JsonSchema.FromText(schemas[kind].GetRawText());
        var payload = JsonSerializer.SerializeToElement(testCase["payload"]);
        var result = schema.Evaluate(payload, new EvaluationOptions { OutputFormat = OutputFormat.List });

        result.IsValid.ShouldBe(expectedValid, label);
        if (expectedValid)
        {
            at.ShouldBeNull(label);
            return;
        }

        at.ShouldNotBeNull(label);
        var pointers = (result.Details ?? [])
            .Where(d => d.Errors is { Count: > 0 })
            .Select(ViolationPointer)
            .Distinct()
            .ToArray();
        pointers.ShouldContain(p => p == at, $"{label}: expected a violation at '{at}', got [{string.Join(", ", pointers)}]");
    }

    // A forbidden extra property is located at the object that forbids it, whichever engine reports it.
    private static string ViolationPointer(EvaluationResults detail)
    {
        var location = detail.InstanceLocation.ToString();
        if (detail.EvaluationPath.ToString().EndsWith("/additionalProperties", StringComparison.Ordinal))
        {
            var cut = location.LastIndexOf('/');
            return cut < 0 ? location : location[..cut];
        }

        return location;
    }

    [Theory]
    [MemberData(nameof(CodeCheckedInvalidExamples))]
    public void Code_checked_invalid_example_gives_its_named_refusal(string name)
    {
        var expectations = JsonNode.Parse(File.ReadAllText(Path.Combine(ExamplesRoot, "invalid", "expectations.json")))!.AsObject();
        var code = expectations[name]!["code"]!.GetValue<string>();

        var violation = FactPayloadSchemaLimits.Check(PayloadSchemas(Path.Combine("invalid", name)));

        violation.ShouldNotBeNull(name);
        violation.Code.ShouldBe(code, name);
    }
}
