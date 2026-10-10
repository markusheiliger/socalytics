using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace SocAlytics.Platform.Contracts.Tests;

internal static partial class ContractPathRule
{
    [GeneratedRegex(@"^contracts/(?<area>[a-z0-9-]+)/(?<name>[a-z0-9-]+)/v(?<major>[1-9][0-9]*)/\k<name>\.schema\.json$")]
    private static partial Regex Named();

    [GeneratedRegex(@"^contracts/common/v(?<major>[1-9][0-9]*)/common\.schema\.json$")]
    private static partial Regex Common();

    public static bool TryMatch(string repositoryPath, out int major)
    {
        var m = Named().Match(repositoryPath);
        if (!m.Success)
        {
            m = Common().Match(repositoryPath);
        }

        major = m.Success ? int.Parse(m.Groups["major"].Value) : 0;
        return m.Success;
    }
}

public class ContractIndexTests
{
    [Fact]
    public void Every_schema_has_exactly_one_index_row_and_no_row_is_orphaned()
    {
        var rows = ContractCatalog.IndexRows;
        var schemas = ContractCatalog.Schemas;

        foreach (var schema in schemas)
        {
            rows.Count(r => r.ArtifactPath == schema.RepositoryPath)
                .ShouldBe(1, $"{schema.RepositoryPath} must have exactly one index row in contracts/README.md");
        }

        foreach (var row in rows)
        {
            schemas.Any(s => s.RepositoryPath == row.ArtifactPath)
                .ShouldBeTrue($"index row '{row.ArtifactPath}' is not a catalog schema");
        }
    }

    [Fact]
    public void Schema_id_matches_repository_path()
    {
        foreach (var schema in ContractCatalog.Schemas)
        {
            schema.Id.ShouldBe("https://socalytics.invalid/" + schema.RepositoryPath, $"$id of {schema.RepositoryPath}");
        }
    }

    [Fact]
    public void Schema_paths_follow_an_admitted_form()
    {
        foreach (var schema in ContractCatalog.Schemas)
        {
            ContractPathRule.TryMatch(schema.RepositoryPath, out _)
                .ShouldBeTrue($"{schema.RepositoryPath} does not match an admitted contract path form");
        }
    }

    [Theory]
    [InlineData("contracts/recordings/recordings-finalized/v1/recordings-finalized.schema.json", true, 1)]
    [InlineData("contracts/common/v1/common.schema.json", true, 1)]
    [InlineData("contracts/common/v2/common.schema.json", true, 2)]
    [InlineData("contracts/common/v1/other.schema.json", false, 0)]
    [InlineData("contracts/common/common.schema.json", false, 0)]
    [InlineData("contracts/a/b/v1/c.schema.json", false, 0)]
    [InlineData("contracts/a/v1/a.schema.json", false, 0)]
    [InlineData("contracts/a/b/v0/b.schema.json", false, 0)]
    public void Path_rule_cases(string path, bool accepted, int major)
    {
        ContractPathRule.TryMatch(path, out var actual).ShouldBe(accepted, path);
        actual.ShouldBe(major, path);
    }

    [Fact]
    public void Releases_have_matching_version_and_schema()
    {
        var schemaPaths = ContractCatalog.Schemas.Select(s => s.RepositoryPath).ToHashSet();
        foreach (var release in ContractCatalog.Releases)
        {
            var m = Regex.Match(release.Version, @"^(?<maj>0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-[0-9A-Za-z.-]+)?(\+[0-9A-Za-z.-]+)?$");
            m.Success.ShouldBeTrue($"{release.RepositoryPath}: '{release.Version}' is not a semantic version");
            var parent = Regex.Match(release.RepositoryPath, @"/v(?<major>[0-9]+)/releases/");
            parent.Success.ShouldBeTrue($"{release.RepositoryPath}: releases folder must sit in a v<major> folder");
            m.Groups["maj"].Value.ShouldBe(parent.Groups["major"].Value, $"{release.RepositoryPath}: version major");
            schemaPaths.ShouldContain(release.SchemaRepositoryPath, $"{release.RepositoryPath}: no catalog schema in its v<major> folder");
        }
    }
}
