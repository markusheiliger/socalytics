using Shouldly;
using SocAlytics.Platform.Infrastructure.Persistence.Migrations;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Migrations;

public sealed class MigrationCatalogTests
{
    [Fact]
    public void PlatformCatalogContainsFoundationSchemaMigration()
    {
        var script = MigrationCatalog.Platform.Scripts.Single(s => s.Identity == "0001_foundation_application_schema");

        script.Sequence.ShouldBe(1);
        script.Area.ShouldBe("foundation");
    }

    [Theory]
    [InlineData("1_foundation_x")]
    [InlineData("0001_Foundation_x")]
    [InlineData("0001_foundation")]
    [InlineData("0001_foundation_x__y")]
    [InlineData("0000_foundation_x")]
    public void MalformedNamesAreRejectedWithoutContent(string identity)
    {
        var ex = Should.Throw<MigrationCatalogException>(() => new MigrationScript(identity, "SECRET-CONTENT"));

        ex.Message.ShouldContain(identity);
        ex.Message.ShouldNotContain("SECRET-CONTENT");
    }

    [Fact]
    public void EmptyScriptIsRejected()
    {
        var ex = Should.Throw<MigrationCatalogException>(() => new MigrationScript("0001_area_empty", " \r\n "));

        ex.Message.ShouldContain("0001_area_empty");
    }

    [Fact]
    public void DuplicateSequenceIsRejectedWithoutContent()
    {
        var ex = Should.Throw<MigrationCatalogException>(() => MigrationCatalog.Create(
        [
            new MigrationScript("0001_area_first", "SECRET-ONE"),
            new MigrationScript("0001_area_second", "SECRET-TWO"),
        ]));

        ex.Message.ShouldNotContain("SECRET");
    }

    [Fact]
    public void DuplicateIdentityIsRejectedWithoutContent()
    {
        var ex = Should.Throw<MigrationCatalogException>(() => MigrationCatalog.Create(
        [
            new MigrationScript("0001_area_first", "SECRET-ONE"),
            new MigrationScript("0001_area_first", "SECRET-TWO"),
        ]));

        ex.Message.ShouldContain("0001_area_first");
        ex.Message.ShouldNotContain("SECRET");
    }

    [Fact]
    public void ScriptsAreOrderedBySequence()
    {
        var catalog = MigrationCatalog.Create(
        [
            new MigrationScript("0003_area_c", "SELECT 3;"),
            new MigrationScript("0001_area_a", "SELECT 1;"),
            new MigrationScript("0002_area_b", "SELECT 2;"),
        ]);

        catalog.Scripts.Select(s => s.Sequence).ShouldBe([1, 2, 3]);
    }
}
