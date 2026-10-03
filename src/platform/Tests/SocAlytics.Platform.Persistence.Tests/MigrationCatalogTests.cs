using Xunit;
using System.Text;
using Shouldly;

namespace SocAlytics.Platform.Persistence.Tests;

public sealed class MigrationCatalogTests
{
    private sealed class Contributor(ModuleKey module, params MigrationDescriptor[] migrations) : IModuleMigrationContributor
    {
        public ModuleKey Module { get; } = module;
        public IReadOnlyList<MigrationDescriptor> Migrations { get; } = migrations;
    }

    private static MigrationDescriptor Migration(ModuleKey module, int sequence, string name, string sql = "SELECT 1;") =>
        new(module, sequence, name, Encoding.UTF8.GetBytes(sql));

    [Fact]
    public void ChecksumIsLowercaseSha256OfExactBytes()
    {
        MigrationChecksum.Compute("abc"u8)
            .ShouldBe("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");
        Migration(ModuleKey.Club, 1, "a").Checksum.ShouldBe(MigrationChecksum.Compute("SELECT 1;"u8));
        Migration(ModuleKey.Club, 1, "a", "SELECT 2;").Checksum.ShouldNotBe(Migration(ModuleKey.Club, 1, "a").Checksum);
    }

    [Fact]
    public void ChecksumReadsEmbeddedResources()
    {
        var assembly = typeof(MigrationCatalogTests).Assembly;
        var name = assembly.GetManifestResourceNames().Single(n => n.EndsWith("sample.sql", StringComparison.Ordinal));
        var descriptor = MigrationDescriptor.FromEmbeddedResource(ModuleKey.Club, 1, "sample", assembly, name);
        descriptor.Checksum.ShouldBe(MigrationChecksum.Compute(descriptor.Content.Span));
        descriptor.Content.Length.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void OrdersByAdoptedModuleThenSequence()
    {
        var catalog = MigrationCatalog.Create([
            new Contributor(ModuleKey.Registry, Migration(ModuleKey.Registry, 2, "b"), Migration(ModuleKey.Registry, 1, "a")),
            new Contributor(ModuleKey.Club, Migration(ModuleKey.Club, 1, "a")),
        ]);

        catalog.Migrations.Select(m => (m.Module.Name, m.Sequence))
            .ShouldBe([("club", 1), ("registry", 1), ("registry", 2)]);
    }

    [Fact]
    public void RejectsDuplicateSequence()
    {
        Should.Throw<InvalidOperationException>(() => MigrationCatalog.Create([
            new Contributor(ModuleKey.Club, Migration(ModuleKey.Club, 1, "a"), Migration(ModuleKey.Club, 1, "b")),
        ]));
    }

    [Fact]
    public void RejectsDuplicateScriptIdentity()
    {
        Should.Throw<InvalidOperationException>(() => MigrationCatalog.Create([
            new Contributor(ModuleKey.Club, Migration(ModuleKey.Club, 1, "a"), Migration(ModuleKey.Club, 2, "a")),
        ]));
    }

    [Fact]
    public void RejectsDuplicateContributorForModule()
    {
        Should.Throw<InvalidOperationException>(() => MigrationCatalog.Create([
            new Contributor(ModuleKey.Club, Migration(ModuleKey.Club, 1, "a")),
            new Contributor(ModuleKey.Club, Migration(ModuleKey.Club, 2, "b")),
        ]));
    }

    [Fact]
    public void RejectsMigrationForAnotherModule()
    {
        Should.Throw<InvalidOperationException>(() => MigrationCatalog.Create([
            new Contributor(ModuleKey.Club, Migration(ModuleKey.Registry, 1, "a")),
        ]));
    }

    [Fact]
    public void RejectsInvalidDescriptors()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => Migration(ModuleKey.Club, 0, "a"));
        Should.Throw<ArgumentException>(() => Migration(ModuleKey.Club, 1, "bad name"));
        Should.Throw<ArgumentException>(() => Migration(ModuleKey.Club, 1, "a", ""));
    }
}
