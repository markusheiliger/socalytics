using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using Shouldly;
using Xunit;

namespace SocAlytics.Platform.Contracts.Tests;

public class PurgeEventContractTests
{
    private const string SchemaPath = "contracts/agent-orchestration/purge-event/v1/purge-event.schema.json";

    private static readonly string Folder =
        Path.Combine(AppContext.BaseDirectory, "contracts", "agent-orchestration", "purge-event", "v1");

    private static EvaluationResults Evaluate(string file)
    {
        var schema = (JsonSchema)ContractCatalog.LoadRegistry().Get(new Uri("https://socalytics.invalid/" + SchemaPath))!;
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

    [Fact]
    public void Valid_example_validates()
    {
        var files = Directory.EnumerateFiles(Path.Combine(Folder, "examples", "valid"), "*.json").ToList();
        files.ShouldNotBeEmpty();
        foreach (var file in files)
        {
            Evaluate(file).IsValid.ShouldBeTrue(Path.GetFileName(file));
        }
    }

    [Fact]
    public void Invalid_examples_fail_with_their_expected_keyword()
    {
        var invalid = Path.Combine(Folder, "examples", "invalid");
        var expectations = JsonNode.Parse(File.ReadAllText(Path.Combine(invalid, "expectations.json")))!.AsObject();
        var files = Directory.EnumerateFiles(invalid, "*.json").Where(f => Path.GetFileName(f) != "expectations.json").ToList();
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
    public void Release_baseline_1_0_0_is_registered_and_equals_the_current_schema()
    {
        ContractCatalog.Schemas.Single(s => s.RepositoryPath == SchemaPath).Document["x-socalytics-version"]!
            .GetValue<string>().ShouldBe("1.0.0");
        var baseline = ContractCatalog.Releases.Single(r =>
            r.RepositoryPath == "contracts/agent-orchestration/purge-event/v1/releases/1.0.0.schema.json");
        baseline.Version.ShouldBe("1.0.0");
        baseline.SchemaRepositoryPath.ShouldBe(SchemaPath);
        File.ReadAllText(Path.Combine(Folder, "releases", "1.0.0.schema.json"))
            .ShouldBe(File.ReadAllText(Path.Combine(Folder, "purge-event.schema.json")));
    }
}
