using System.Text.Json.Nodes;
using Shouldly;
using Xunit;

namespace SocAlytics.Platform.Contracts.Tests.Compatibility;

public class SchemaCompatibilityTests
{
    private static readonly string FixtureRoot = Path.Combine(AppContext.BaseDirectory, "Compatibility", "BreakingRevisions");

    private static readonly string[] AnalysisAndCommonSchemas =
    [
        "contracts/common/v1/common.schema.json",
        "contracts/analysis/analyst-job/v1/analyst-job.schema.json",
        "contracts/analysis/attempt-completion/v1/attempt-completion.schema.json",
        "contracts/analysis/run-state-changed/v1/run-state-changed.schema.json",
    ];

    public static TheoryData<string> BreakingRevisions() =>
    [
        "removed-property",
        "renamed-property",
        "newly-required-property",
        "narrowed-enum",
        "narrowed-pattern",
        "newly-closed-object",
    ];

    public static TheoryData<string> ReleasedSchemas()
    {
        var data = new TheoryData<string>();
        foreach (var path in ContractCatalog.Releases.Select(r => r.SchemaRepositoryPath).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            data.Add(path);
        }

        return data;
    }

    [Fact]
    public void Analysis_and_common_baselines_are_released_not_current()
    {
        var releases = ContractCatalog.Releases.Select(r => r.RepositoryPath).ToList();
        var current = ContractCatalog.Schemas.Select(s => s.RepositoryPath).ToList();
        foreach (var schema in AnalysisAndCommonSchemas)
        {
            var release = schema[..schema.LastIndexOf('/')] + "/releases/1.0.0.schema.json";
            releases.ShouldContain(release);
            current.ShouldNotContain(release);
            File.ReadAllBytes(ContractCatalog.Releases.Single(r => r.RepositoryPath == release).FullPath)
                .ShouldBe(File.ReadAllBytes(ContractCatalog.Schemas.Single(s => s.RepositoryPath == schema).FullPath));
        }

        ContractCatalog.LoadRegistry();
    }

    [Fact]
    public void Infrastructure_embedding_excludes_releases()
    {
        var csproj = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "platform", "SocAlytics.Platform.Infrastructure", "SocAlytics.Platform.Infrastructure.csproj"));
        csproj.ShouldContain("contracts\\**\\releases\\**");
    }

    [Theory]
    [MemberData(nameof(ReleasedSchemas))]
    public void Current_schema_is_compatible_with_every_released_copy(string schemaPath)
    {
        var current = ContractCatalog.Schemas.Single(s => s.RepositoryPath == schemaPath);
        var currentDocuments = ContractCatalog.Schemas
            .Where(s => s.Id is not null)
            .ToDictionary(s => s.Id!, s => s.Document);

        var releases = ContractCatalog.Releases.Where(r => r.SchemaRepositoryPath == schemaPath).ToList();
        foreach (var version in releases.Select(r => r.Version).Distinct())
        {
            var baselineDocuments = ContractCatalog.Releases
                .Where(r => r.Version == version && r.Document["$id"] is not null)
                .ToDictionary(r => r.Document["$id"]!.GetValue<string>(), r => r.Document);
            var release = releases.First(r => r.Version == version);

            SchemaCompatibilityChecker.Check(release.Document, current.Document, baselineDocuments, currentDocuments)
                .ShouldBeEmpty($"{schemaPath} breaks released version {version}");
        }
    }

    [Theory]
    [MemberData(nameof(BreakingRevisions))]
    public void Breaking_revision_fails(string fixture)
    {
        SchemaCompatibilityChecker.Check(Load("baseline"), Load(fixture)).ShouldNotBeEmpty();
    }

    [Fact]
    public void Additive_revision_passes()
    {
        SchemaCompatibilityChecker.Check(Load("baseline"), Load("additive-revision")).ShouldBeEmpty();
    }

    private static JsonNode Load(string name) => JsonNode.Parse(File.ReadAllText(Path.Combine(FixtureRoot, name + ".json")))!;

    private static string FindRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "contracts")) || dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src")))
        {
            dir = dir.Parent;
        }

        return dir!.FullName;
    }
}
