using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using Shouldly;
using Xunit;

namespace SocAlytics.Platform.Contracts.Tests;

public class AnalysisExampleTests
{
    private static readonly string AnalysisRoot = Path.Combine(AppContext.BaseDirectory, "contracts", "analysis");

    private static readonly IReadOnlyList<string> SchemaDirectories = ContractCatalog.AnalysisExampleContracts;

    private sealed record Outcome(string File, bool Valid, string[] Keywords);

    private static List<(string File, string SchemaFile, bool ExpectValid, string? Keyword)> Discover()
    {
        var items = new List<(string, string, bool, string?)>();
        foreach (var name in SchemaDirectories)
        {
            var version = Path.Combine(AnalysisRoot, name, "v1");
            var schemaFile = Path.Combine(version, name + ".schema.json");
            foreach (var file in Directory.EnumerateFiles(Path.Combine(version, "examples", "valid"), "*.json").Order(StringComparer.Ordinal))
            {
                items.Add((file, schemaFile, true, null));
            }

            var invalid = Path.Combine(version, "examples", "invalid");
            var expectations = JsonNode.Parse(File.ReadAllText(Path.Combine(invalid, "expectations.json")))!.AsObject();
            foreach (var file in Directory.EnumerateFiles(invalid, "*.json").Where(f => Path.GetFileName(f) != "expectations.json").Order(StringComparer.Ordinal))
            {
                var keyword = expectations[Path.GetFileName(file)]?.GetValue<string>();
                keyword.ShouldNotBeNull($"{Path.GetFileName(file)} declares no expected keyword");
                items.Add((file, schemaFile, false, keyword));
            }
        }

        return items;
    }

    private static List<Outcome> Evaluate()
    {
        var registry = ContractCatalog.LoadRegistry();
        var outcomes = new List<Outcome>();
        foreach (var (file, schemaFile, _, _) in Discover())
        {
            var id = JsonNode.Parse(File.ReadAllText(schemaFile))!["$id"]!.GetValue<string>();
            var schema = (JsonSchema)registry.Get(new Uri(id))!;
            var instance = JsonDocument.Parse(File.ReadAllText(file)).RootElement;
            var result = schema.Evaluate(instance, new EvaluationOptions { OutputFormat = OutputFormat.List });
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

            outcomes.Add(new Outcome(file, result.IsValid, keywords.ToArray()));
        }

        return outcomes;
    }

    [Fact]
    public void Corpus_covers_every_analysis_schema()
    {
        var items = Discover();
        foreach (var name in SchemaDirectories)
        {
            items.ShouldContain(i => i.File.Contains(Path.Combine(name, "v1", "examples", "valid")));
            items.ShouldContain(i => i.File.Contains(Path.Combine(name, "v1", "examples", "invalid")));
        }

        items.Count(i => i.ExpectValid).ShouldBeGreaterThanOrEqualTo(5);
    }

    [Fact]
    public void Valid_examples_pass_and_invalid_examples_fail_with_their_expected_keyword()
    {
        var expected = Discover().ToDictionary(i => i.File);
        foreach (var outcome in Evaluate())
        {
            var item = expected[outcome.File];
            var name = Path.GetRelativePath(AnalysisRoot, outcome.File);
            outcome.Valid.ShouldBe(item.ExpectValid, name);
            if (!item.ExpectValid)
            {
                outcome.Keywords.ShouldContain(item.Keyword!, name);
            }
        }
    }

    [Fact]
    public void Attempt_and_job_1_1_0_examples_are_present_with_their_expected_keywords()
    {
        var items = Discover();
        string[] valid =
        [
            "analyst-attempt/v1/examples/valid/attempt.json",
            "analyst-job/v1/examples/valid/segment-job-with-window.json",
            "analyst-job/v1/examples/valid/match-job-with-workflow.json",
        ];
        foreach (var path in valid)
        {
            items.ShouldContain(i => i.ExpectValid && i.File.EndsWith(path.Replace('/', Path.DirectorySeparatorChar), StringComparison.Ordinal), path);
        }

        (string Path, string Keyword)[] invalid =
        [
            ("analyst-attempt/v1/examples/invalid/missing-attempt-number.json", "required"),
            ("analyst-attempt/v1/examples/invalid/carries-fencing-token.json", "additionalProperties"),
            ("analyst-attempt/v1/examples/invalid/zero-attempt-number.json", "minimum"),
            ("analyst-job/v1/examples/invalid/segment-window-on-match-job.json", "not"),
            ("analyst-job/v1/examples/invalid/segment-window-unknown-property.json", "additionalProperties"),
        ];
        foreach (var (path, keyword) in invalid)
        {
            items.ShouldContain(i => !i.ExpectValid && i.Keyword == keyword && i.File.EndsWith(path.Replace('/', Path.DirectorySeparatorChar), StringComparison.Ordinal), path);
        }
    }

    [Fact]
    public void Analyst_job_1_1_0_is_same_major_compatible_with_its_1_0_0_baseline()
    {
        var dir = Path.Combine(AnalysisRoot, "analyst-job", "v1");
        var baselineFile = Path.Combine(dir, "releases", "1.0.0.schema.json");
        var currentFile = Path.Combine(dir, "analyst-job.schema.json");
        var baseline = JsonNode.Parse(File.ReadAllText(baselineFile))!;
        var current = JsonNode.Parse(File.ReadAllText(currentFile))!;

        current["required"]!.AsArray().Select(n => n!.GetValue<string>()).Order()
            .ShouldBe(baseline["required"]!.AsArray().Select(n => n!.GetValue<string>()).Order());
        foreach (var property in baseline["properties"]!.AsObject().Select(p => p.Key))
        {
            current["properties"]!.AsObject().ContainsKey(property).ShouldBeTrue($"analyst-job: property {property} removed");
        }

        foreach (var example in new[] { "segment-job.json", "match-job-with-segment-barrier.json" })
        {
            var file = Path.Combine(dir, "examples", "valid", example);
            ContractCatalog.EvaluateAgainstRelease(baselineFile, file).IsValid.ShouldBeTrue($"analyst-job/{example} under 1.0.0");
            ContractCatalog.EvaluateAgainstRelease(currentFile, file).IsValid.ShouldBeTrue($"analyst-job/{example} under 1.1.0");
        }
    }

    [Fact]
    public void Evaluating_the_corpus_twice_gives_identical_outcomes()
    {
        var first = Evaluate();
        var second = Evaluate();

        second.Count.ShouldBe(first.Count);
        for (var i = 0; i < first.Count; i++)
        {
            second[i].File.ShouldBe(first[i].File);
            second[i].Valid.ShouldBe(first[i].Valid);
            second[i].Keywords.ShouldBe(first[i].Keywords);
        }
    }
}
