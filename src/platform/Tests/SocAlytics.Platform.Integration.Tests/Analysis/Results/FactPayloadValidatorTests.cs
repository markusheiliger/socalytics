using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Shouldly;
using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Analysis.Results;
using SocAlytics.Platform.Infrastructure.Registry;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Analysis.Results;

public sealed class FactPayloadValidatorTests
{
    private static readonly string ManifestContractRoot = Path.Combine(
        RepositoryRoot(), "contracts", "analysis", "analyst-manifest", "v1");

    private static readonly JsonElement Cases = Load(Path.Combine(ManifestContractRoot, "examples", "fact-payloads.json"));

    public static TheoryData<int> CaseIndexes()
    {
        var data = new TheoryData<int>();
        for (var i = 0; i < Cases.GetArrayLength(); i++)
        {
            data.Add(i);
        }

        return data;
    }

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "contracts", "analysis", "analyst-manifest")))
        {
            dir = dir.Parent;
        }

        return (dir ?? throw new InvalidOperationException("Repository root not found.")).FullName;
    }

    private static JsonElement Load(string path) => JsonDocument.Parse(File.ReadAllText(path)).RootElement.Clone();

    private static FactPayloadValidator NewValidator(TimeSpan? maxDuration = null) =>
        new(Options.Create(new AnalysisResultOptions
        {
            PayloadValidation = new PayloadValidationOptions { MaxDuration = maxDuration ?? TimeSpan.FromSeconds(5) },
        }));

    private static IReadOnlyDictionary<string, JsonElement> Schemas(string manifestExample)
    {
        var manifest = Load(Path.Combine(ManifestContractRoot, "examples", manifestExample.Replace('/', Path.DirectorySeparatorChar)));
        return manifest.GetProperty("output").GetProperty("factPayloadSchemas").EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);
    }

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    private static string Nested(int depth)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < depth; i++)
        {
            builder.Append("{\"a\":");
        }

        builder.Append('1');
        builder.Append('}', depth);
        return builder.ToString();
    }

    [Fact]
    public void Examples_cover_valid_and_invalid_cases()
    {
        Cases.GetArrayLength().ShouldBeGreaterThan(0);
        Cases.EnumerateArray().Any(c => c.GetProperty("valid").GetBoolean()).ShouldBeTrue();
        Cases.EnumerateArray().Any(c => !c.GetProperty("valid").GetBoolean()).ShouldBeTrue();
    }

    [Theory]
    [MemberData(nameof(CaseIndexes))]
    public void Every_example_case_gives_its_verdict_and_pointer(int index)
    {
        var testCase = Cases[index];
        var kind = testCase.GetProperty("kind").GetString()!;
        var payload = testCase.GetProperty("payload");
        var schemas = Schemas(testCase.GetProperty("manifest").GetString()!);

        var result = NewValidator().Validate(schemas, [new FactPayloadCandidate(kind, payload)]);

        if (testCase.GetProperty("valid").GetBoolean())
        {
            result.IsValid.ShouldBeTrue($"case {index}: {result}");
            result.Outcome.ShouldBe(FactPayloadValidationOutcome.Valid);
            return;
        }

        result.Outcome.ShouldBe(FactPayloadValidationOutcome.Invalid, $"case {index}");
        result.Kind.ShouldBe(kind);
        result.FactIndex.ShouldBe(0);
        result.ViolationCount.ShouldBe(1);
        result.Pointer.ShouldBe(testCase.GetProperty("at").GetString(), $"case {index}");
    }

    [Fact]
    public void First_violation_names_kind_fact_index_and_counts_every_nonconforming_fact()
    {
        var schemas = Schemas("valid/segment-with-payload-schemas.json");
        var good = Json("{\"sharpness\":0.4}");
        var bad = Json("{\"sharpness\":1.5}");

        var result = NewValidator().Validate(schemas,
        [
            new FactPayloadCandidate("frame-quality", good),
            new FactPayloadCandidate("frame-quality", bad),
            new FactPayloadCandidate("frame-quality", bad),
        ]);

        result.Outcome.ShouldBe(FactPayloadValidationOutcome.Invalid);
        result.Kind.ShouldBe("frame-quality");
        result.FactIndex.ShouldBe(1);
        result.Pointer.ShouldBe("/sharpness");
        result.ViolationCount.ShouldBe(2);
    }

    [Fact]
    public void Undeclared_kind_is_reported_without_a_pointer()
    {
        var schemas = Schemas("valid/segment-with-payload-schemas.json");

        var result = NewValidator().Validate(schemas, [new FactPayloadCandidate("not-declared", Json("{}"))]);

        result.Outcome.ShouldBe(FactPayloadValidationOutcome.KindUndeclared);
        result.Kind.ShouldBe("not-declared");
        result.FactIndex.ShouldBe(0);
        result.Pointer.ShouldBe(string.Empty);
    }

    [Fact]
    public void Time_bound_is_reached_by_a_pathological_payload_without_a_hang()
    {
        var schemas = new Dictionary<string, JsonElement>
        {
            ["probe"] = Json("{\"type\":\"array\",\"items\":{\"type\":\"array\",\"items\":{\"type\":\"integer\"}}}"),
        };
        var rows = string.Join(',', Enumerable.Repeat("[" + string.Join(',', Enumerable.Range(0, 200)) + "]", 200));
        var payload = Json("[" + rows + "]");
        var facts = Enumerable.Range(0, 100_000).Select(_ => new FactPayloadCandidate("probe", payload)).ToList();

        var watch = Stopwatch.StartNew();
        var result = NewValidator(TimeSpan.FromMilliseconds(50)).Validate(schemas, facts);
        watch.Stop();

        result.Outcome.ShouldBe(FactPayloadValidationOutcome.BoundReached);
        result.Kind.ShouldBe("probe");
        (result.FactIndex ?? 0).ShouldBeGreaterThan(0);
        result.Pointer.ShouldBe(string.Empty);
        watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void Time_bound_keeps_an_earlier_violation_as_the_verdict()
    {
        var schemas = Schemas("valid/segment-with-payload-schemas.json");
        var bad = Json("{\"sharpness\":1.5}");
        var facts = Enumerable.Range(0, 200_000).Select(_ => new FactPayloadCandidate("frame-quality", bad)).ToList();

        var result = NewValidator(TimeSpan.FromMilliseconds(20)).Validate(schemas, facts);

        result.Outcome.ShouldBe(FactPayloadValidationOutcome.Invalid);
        result.FactIndex.ShouldBe(0);
        result.Pointer.ShouldBe("/sharpness");
        result.ViolationCount.ShouldBeLessThan(facts.Count);
    }

    [Fact]
    public void Memory_bound_holds_at_instance_depth_32()
    {
        FactPayloadValidator.MaxInstanceDepth.ShouldBe(32);
        var schemas = new Dictionary<string, JsonElement> { ["deep"] = Json("{\"type\":\"object\"}") };

        var atLimit = NewValidator().Validate(schemas, [new FactPayloadCandidate("deep", Json(Nested(32)))]);
        var beyond = NewValidator().Validate(schemas, [new FactPayloadCandidate("deep", Json(Nested(33)))]);

        atLimit.IsValid.ShouldBeTrue();
        beyond.Outcome.ShouldBe(FactPayloadValidationOutcome.BoundReached);
        beyond.Kind.ShouldBe("deep");
        beyond.FactIndex.ShouldBe(0);
        beyond.Pointer.ShouldBe(string.Empty);
    }

    [Fact]
    public void Payloads_are_returned_unmodified()
    {
        var schemas = Schemas("valid/segment-with-payload-schemas.json");
        var cases = Cases.EnumerateArray()
            .Where(c => c.GetProperty("manifest").GetString() == "valid/segment-with-payload-schemas.json")
            .ToList();
        var candidates = cases.Select(c => new FactPayloadCandidate(c.GetProperty("kind").GetString()!, c.GetProperty("payload"))).ToList();
        var before = candidates.Select(c => c.Payload.GetRawText()).ToList();

        NewValidator().Validate(schemas, candidates);

        candidates.Select(c => c.Payload.GetRawText()).ShouldBe(before);
    }
}
