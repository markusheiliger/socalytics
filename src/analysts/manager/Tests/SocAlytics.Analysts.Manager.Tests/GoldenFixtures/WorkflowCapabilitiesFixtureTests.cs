using System.Text.Json;
using Json.Schema;
using Shouldly;
using Xunit;

namespace SocAlytics.Analysts.Manager.Tests.GoldenFixtures;

public class WorkflowCapabilitiesFixtureTests
{
    private static readonly string Folder =
        Path.Combine(AppContext.BaseDirectory, "contracts", "analyst-manager", "workflow-capabilities", "v1");

    private static readonly JsonSchema Schema = JsonSchema.FromText(
        File.ReadAllText(Path.Combine(Folder, "workflow-capabilities.schema.json")));

    public static TheoryData<string> ValidFiles() => Files("valid");

    public static TheoryData<string> InvalidFiles() => Files("invalid");

    private static TheoryData<string> Files(string kind)
    {
        var data = new TheoryData<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(Folder, "examples", kind), "*.json").Order(StringComparer.Ordinal))
        {
            data.Add(Path.GetFileName(file));
        }

        return data;
    }

    private static bool IsValid(string kind, string file)
    {
        var instance = JsonDocument.Parse(File.ReadAllText(Path.Combine(Folder, "examples", kind, file))).RootElement;
        return Schema.Evaluate(instance, new EvaluationOptions { OutputFormat = OutputFormat.List }).IsValid;
    }

    [Theory]
    [MemberData(nameof(ValidFiles))]
    public void Valid_example_validates(string file) => IsValid("valid", file).ShouldBeTrue(file);

    [Theory]
    [MemberData(nameof(InvalidFiles))]
    public void Invalid_example_is_rejected(string file) => IsValid("invalid", file).ShouldBeFalse(file);

    [Fact]
    public void Examples_cover_decimal_cpus_and_reject_digests()
    {
        Files("valid").Count.ShouldBeGreaterThanOrEqualTo(2);
        Files("invalid").Count.ShouldBeGreaterThanOrEqualTo(4);
        IsValid("invalid", "response.numeric-cpus.json").ShouldBeFalse();
        IsValid("invalid", "response.image-digest.json").ShouldBeFalse();
    }
}
