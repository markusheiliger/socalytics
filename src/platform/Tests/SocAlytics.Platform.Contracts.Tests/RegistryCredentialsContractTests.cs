using System.Text.Json;
using Json.Schema;
using Shouldly;
using Xunit;

namespace SocAlytics.Platform.Contracts.Tests;

public class RegistryCredentialsContractTests
{
    private const string SchemaPath = "contracts/analyst-manager/registry-credentials/v1/registry-credentials.schema.json";

    private static readonly string VersionRoot =
        Path.Combine(AppContext.BaseDirectory, "contracts", "analyst-manager", "registry-credentials", "v1");

    private static readonly string Folder = Path.Combine(VersionRoot, "examples");

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
        InvalidFiles().Count.ShouldBeGreaterThanOrEqualTo(4);
    }

    [Theory]
    [MemberData(nameof(ValidFiles))]
    public void Valid_example_validates(string file) => IsValid("valid", file).ShouldBeTrue(file);

    [Theory]
    [MemberData(nameof(InvalidFiles))]
    public void Invalid_example_is_rejected(string file) => IsValid("invalid", file).ShouldBeFalse(file);

    [Fact]
    public void Released_copy_equals_the_schema()
    {
        File.ReadAllBytes(Path.Combine(VersionRoot, "releases", "1.0.0.schema.json"))
            .ShouldBe(File.ReadAllBytes(Path.Combine(VersionRoot, "registry-credentials.schema.json")));
    }
}
