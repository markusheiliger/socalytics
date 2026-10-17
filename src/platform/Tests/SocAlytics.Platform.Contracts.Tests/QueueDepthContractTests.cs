using System.Text.Json;
using Json.Schema;
using Shouldly;
using Xunit;

namespace SocAlytics.Platform.Contracts.Tests;

public class QueueDepthContractTests
{
    private const string SchemaPath = "contracts/analyst-manager/queue-depth/v1/queue-depth.schema.json";

    private static readonly string Folder =
        Path.Combine(AppContext.BaseDirectory, "contracts", "analyst-manager", "queue-depth", "v1", "examples");

    public static TheoryData<string> Files(string kind)
    {
        var data = new TheoryData<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(Folder, kind), "*.json").Order(StringComparer.Ordinal))
        {
            data.Add(Path.GetFileName(file));
        }

        return data;
    }

    public static TheoryData<string> ValidFiles() => Files("valid");

    public static TheoryData<string> InvalidFiles() => Files("invalid");

    private static bool IsValid(string kind, string file)
    {
        var schema = (JsonSchema)ContractCatalog.LoadRegistry().Get(new Uri("https://socalytics.invalid/" + SchemaPath))!;
        var instance = JsonDocument.Parse(File.ReadAllText(Path.Combine(Folder, kind, file))).RootElement;
        return schema.Evaluate(instance, new EvaluationOptions { OutputFormat = OutputFormat.List }).IsValid;
    }

    [Fact]
    public void Valid_and_invalid_examples_exist()
    {
        ValidFiles().Count.ShouldBeGreaterThanOrEqualTo(3);
        InvalidFiles().Count.ShouldBeGreaterThanOrEqualTo(1);
    }

    [Theory]
    [MemberData(nameof(ValidFiles))]
    public void Valid_example_validates(string file) => IsValid("valid", file).ShouldBeTrue(file);

    [Theory]
    [MemberData(nameof(InvalidFiles))]
    public void Invalid_example_is_rejected(string file) => IsValid("invalid", file).ShouldBeFalse(file);

    [Theory]
    [MemberData(nameof(ValidFiles))]
    public void Valid_example_is_ordered_by_id_and_totals_the_counts(string file)
    {
        var root = JsonDocument.Parse(File.ReadAllText(Path.Combine(Folder, "valid", file))).RootElement;
        var capabilities = root.GetProperty("capabilities").EnumerateArray().ToList();
        capabilities.Select(c => c.GetProperty("id").GetString()!).ToList()
            .ShouldBe(capabilities.Select(c => c.GetProperty("id").GetString()!).Order(StringComparer.Ordinal).ToList(), file);
        root.GetProperty("total").GetInt32().ShouldBe(capabilities.Sum(c => c.GetProperty("waiting").GetInt32()), file);
    }
}
