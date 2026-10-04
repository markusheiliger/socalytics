using System.Reflection;
using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;
using SocAlytics.Platform.Persistence;

namespace SocAlytics.Platform.Persistence.Tests;

public sealed class PersistenceFoundationTests
{
    private static readonly string[] AdoptedKeys =
        ["club", "identity_access", "recordings", "registry", "analysis", "agent_orchestration"];

    private sealed class Contributor(PersistenceModuleKey module, params MigrationDescriptor[] migrations)
        : IMigrationContributor
    {
        public PersistenceModuleKey Module { get; } = module;

        public IReadOnlyList<MigrationDescriptor> GetMigrations() => migrations;
    }

    private static MigrationDescriptor Migration(PersistenceModuleKey module, int sequence, string identity) =>
        new(module, sequence, identity, "SELECT 1;"u8.ToArray());

    [Fact]
    public void ModuleKeysAreFixedAndOrdered()
    {
        PersistenceModuleKey.All.Select(m => m.Key).ShouldBe(AdoptedKeys);
        PersistenceModuleKey.Parse("recordings").ShouldBeSameAs(PersistenceModuleKey.Recordings);
        Should.Throw<ArgumentException>(() => PersistenceModuleKey.Parse("other"));
        typeof(PersistenceModuleKey).GetConstructors().ShouldBeEmpty();
    }

    [Fact]
    public void ChecksumIsSha256OfContent()
    {
        var bytes = "CREATE SCHEMA club;"u8.ToArray();
        var descriptor = new MigrationDescriptor(PersistenceModuleKey.Club, 1, "0001-schema", bytes);

        descriptor.Checksum.ShouldBe(Convert.ToHexStringLower(SHA256.HashData(bytes)));
        bytes[0] = 0;
        descriptor.Checksum.ShouldBe(MigrationDescriptor.ComputeChecksum(descriptor.Content.Span));
    }

    [Fact]
    public void EmbeddedResourceChecksumIsCalculated()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var name = assembly.GetManifestResourceNames().Single(n => n.EndsWith("sample.sql", StringComparison.Ordinal));

        var descriptor = MigrationDescriptor.FromEmbeddedResource(PersistenceModuleKey.Club, 1, "sample", assembly, name);

        descriptor.Checksum.ShouldBe(MigrationDescriptor.ComputeChecksum(descriptor.Content.Span));
        Should.Throw<InvalidOperationException>(() =>
            MigrationDescriptor.FromEmbeddedResource(PersistenceModuleKey.Club, 1, "x", assembly, "missing.sql"));
    }

    [Fact]
    public void CatalogOrdersByModuleThenSequence()
    {
        var catalog = new MigrationCatalog([
            new Contributor(PersistenceModuleKey.Registry, Migration(PersistenceModuleKey.Registry, 1, "a")),
            new Contributor(PersistenceModuleKey.Club,
                Migration(PersistenceModuleKey.Club, 2, "b"), Migration(PersistenceModuleKey.Club, 1, "a"))]);

        catalog.Migrations.Select(m => (m.Module.Key, m.Sequence))
            .ShouldBe([("club", 1), ("club", 2), ("registry", 1)]);
    }

    [Fact]
    public void CatalogRejectsDuplicatesAndForeignMigrations()
    {
        Should.Throw<InvalidOperationException>(() => new MigrationCatalog([
            new Contributor(PersistenceModuleKey.Club,
                Migration(PersistenceModuleKey.Club, 1, "a"), Migration(PersistenceModuleKey.Club, 2, "a"))]));
        Should.Throw<InvalidOperationException>(() => new MigrationCatalog([
            new Contributor(PersistenceModuleKey.Club,
                Migration(PersistenceModuleKey.Club, 1, "a"), Migration(PersistenceModuleKey.Club, 1, "b"))]));
        Should.Throw<InvalidOperationException>(() => new MigrationCatalog([
            new Contributor(PersistenceModuleKey.Club, Migration(PersistenceModuleKey.Registry, 1, "a"))]));
        Should.Throw<InvalidOperationException>(() => new MigrationCatalog([
            new Contributor(PersistenceModuleKey.Club),
            new Contributor(PersistenceModuleKey.Club)]));
    }

    [Fact]
    public void BootstrapConnectionIsNotAvailableToModuleServices()
    {
        var persistence = typeof(PersistenceModuleKey).Assembly;
        var bootstrap = persistence.GetType("SocAlytics.Platform.Persistence.BootstrapConnectionSource");

        bootstrap.ShouldNotBeNull();
        bootstrap.IsPublic.ShouldBeFalse();
        persistence.GetExportedTypes()
            .SelectMany(t => t.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            .OfType<MethodInfo>()
            .Where(m => m.ReturnType == bootstrap || m.GetParameters().Any(p => p.ParameterType == bootstrap))
            .ShouldBeEmpty();

        var services = new ServiceCollection()
            .AddPlatformPersistence("Host=localhost;Database=x")
            .AddModulePersistence(new Contributor(PersistenceModuleKey.Club));
        using var provider = services.BuildServiceProvider();

        var factory = provider.GetRequiredKeyedService<IModuleConnectionFactory>("club");
        factory.Module.ShouldBeSameAs(PersistenceModuleKey.Club);
        factory.GetType().GetFields(BindingFlags.NonPublic | BindingFlags.Instance)
            .ShouldNotContain(f => f.FieldType == bootstrap);
    }
}
