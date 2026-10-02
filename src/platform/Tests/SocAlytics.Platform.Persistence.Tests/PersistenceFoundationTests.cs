using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;
using SocAlytics.Platform.Persistence;

namespace SocAlytics.Platform.Persistence.Tests;

public sealed class PersistenceFoundationTests
{
    private static readonly byte[] Script = "SELECT 1;"u8.ToArray();

    [Fact]
    public void ModuleKeysAreTheSixAdoptedSchemasInOrder()
    {
        PersistenceModuleKey.All.Select(k => k.Name).ShouldBe(
            ["club", "identity_access", "recordings", "registry", "analysis", "agent_orchestration"]);
        typeof(PersistenceModuleKey).GetConstructors(BindingFlags.Public | BindingFlags.Instance).ShouldBeEmpty();
        PersistenceModuleKey.Club.RuntimeRoleName.ShouldBe("club_runtime");
        PersistenceModuleKey.Club.OwnerRoleName.ShouldBe("club_owner");
    }

    [Fact]
    public void ChecksumIsLowercaseSha256OfExactBytes()
    {
        var descriptor = new MigrationDescriptor(PersistenceModuleKey.Club, 1, "0001_init", Script);

        descriptor.Checksum.ShouldBe(MigrationDescriptor.ComputeChecksum(Script));
        descriptor.Checksum.Length.ShouldBe(64);
        descriptor.Checksum.ShouldBe(descriptor.Checksum.ToLowerInvariant());
        new MigrationDescriptor(PersistenceModuleKey.Club, 1, "0001_init", "SELECT 2;"u8).Checksum
            .ShouldNotBe(descriptor.Checksum);
    }

    [Fact]
    public void EmbeddedResourceChecksumMatchesResourceBytes()
    {
        var assembly = typeof(PersistenceFoundationTests).Assembly;
        var name = assembly.GetManifestResourceNames().Single(n => n.EndsWith("sample.sql", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name)!;
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);

        var descriptor = MigrationDescriptor.FromEmbeddedResource(PersistenceModuleKey.Club, 1, "sample", assembly, name);

        descriptor.Checksum.ShouldBe(MigrationDescriptor.ComputeChecksum(buffer.ToArray()));
        Should.Throw<InvalidOperationException>(() =>
            MigrationDescriptor.FromEmbeddedResource(PersistenceModuleKey.Club, 1, "x", assembly, "missing.sql"));
    }

    [Fact]
    public void InvalidDescriptorsAreRejected()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new MigrationDescriptor(PersistenceModuleKey.Club, 0, "a", Script));
        Should.Throw<ArgumentException>(() => new MigrationDescriptor(PersistenceModuleKey.Club, 1, " ", Script));
        Should.Throw<ArgumentException>(() => new MigrationDescriptor(PersistenceModuleKey.Club, 1, "a", ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void CatalogOrdersByModuleThenSequence()
    {
        var catalog = MigrationCatalog.Create(
        [
            Contributor(PersistenceModuleKey.Registry, (2, "b"), (1, "a")),
            Contributor(PersistenceModuleKey.Club, (1, "a")),
        ]);

        catalog.Migrations.Select(m => $"{m.Module}:{m.Sequence}").ShouldBe(["club:1", "registry:1", "registry:2"]);
    }

    [Fact]
    public void CatalogRejectsDuplicateSequencesIdentitiesAndForeignModules()
    {
        Should.Throw<InvalidOperationException>(() => MigrationCatalog.Create(
            [Contributor(PersistenceModuleKey.Club, (1, "a"), (1, "b"))]));
        Should.Throw<InvalidOperationException>(() => MigrationCatalog.Create(
            [Contributor(PersistenceModuleKey.Club, (1, "a"), (2, "a"))]));
        Should.Throw<InvalidOperationException>(() => MigrationCatalog.Create(
            [Contributor(PersistenceModuleKey.Club, (1, "a")), Contributor(PersistenceModuleKey.Club, (2, "b"))]));
        Should.Throw<InvalidOperationException>(() => MigrationCatalog.Create(
            [new StubContributor(PersistenceModuleKey.Club,
                [new MigrationDescriptor(PersistenceModuleKey.Registry, 1, "a", Script)])]));
    }

    [Fact]
    public void BootstrapConnectionIsNotReachableByModuleServices()
    {
        typeof(PersistenceModuleKey).Assembly.GetExportedTypes()
            .Where(t => t.Name.Contains("Bootstrap", StringComparison.Ordinal) && t != typeof(PlatformPersistenceOptions))
            .ShouldBeEmpty();

        var services = new ServiceCollection();
        services.AddPlatformPersistence(o => o.BootstrapConnectionString = "Host=localhost;Database=x");
        services.AddModulePersistence(PersistenceModuleKey.Club);

        services.Select(d => d.ServiceType).Where(t => t.IsPublic && t.Name.Contains("Bootstrap")).ShouldBeEmpty();

        using var provider = services.BuildServiceProvider();
        provider.GetService<Npgsql.NpgsqlDataSource>().ShouldBeNull();
        var factory = provider.GetRequiredKeyedService<IModuleConnectionFactory>(PersistenceModuleKey.Club);
        factory.Module.ShouldBe(PersistenceModuleKey.Club);
        factory.GetType().GetProperties().Select(p => p.PropertyType).ShouldNotContain(typeof(Npgsql.NpgsqlDataSource));
    }

    [Fact]
    public void RegistrationRequiresBootstrapConnectionString()
    {
        Should.Throw<ArgumentException>(() => new ServiceCollection().AddPlatformPersistence(_ => { }));
    }

    private static StubContributor Contributor(PersistenceModuleKey key, params (int Sequence, string Name)[] scripts) =>
        new(key, scripts.Select(s => new MigrationDescriptor(key, s.Sequence, s.Name, Script)).ToArray());

    private sealed class StubContributor(PersistenceModuleKey module, IReadOnlyList<MigrationDescriptor> migrations)
        : IMigrationContributor
    {
        public PersistenceModuleKey Module { get; } = module;

        public IReadOnlyList<MigrationDescriptor> GetMigrations() => migrations;
    }
}
