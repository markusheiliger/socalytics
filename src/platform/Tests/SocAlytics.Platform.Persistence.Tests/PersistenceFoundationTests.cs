using Xunit;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using SocAlytics.Platform.Persistence;

namespace SocAlytics.Platform.Persistence.Tests;

public sealed class PersistenceFoundationTests
{
    [Fact]
    public void Module_keys_are_the_fixed_adopted_set_in_order()
    {
        PersistenceModuleKey.All.Select(k => k.Schema).ShouldBe(
            ["club", "identity_access", "recordings", "registry", "analysis", "agent_orchestration"]);
        PersistenceModuleKey.FromSchema("club").ShouldBeSameAs(PersistenceModuleKey.Club);
        Should.Throw<ArgumentException>(() => PersistenceModuleKey.FromSchema("other"));
        typeof(PersistenceModuleKey).GetConstructors().ShouldBeEmpty();
    }

    [Fact]
    public void Checksum_is_lowercase_sha256_of_content()
    {
        var descriptor = new MigrationDescriptor(PersistenceModuleKey.Club, 1, "init", "abc"u8.ToArray());

        descriptor.Checksum.ShouldBe("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");
    }

    [Fact]
    public void Catalog_orders_by_module_then_sequence()
    {
        var catalog = MigrationCatalog.Create(
        [
            new Contributor(PersistenceModuleKey.Registry, Migration(PersistenceModuleKey.Registry, 1, "r1")),
            new Contributor(PersistenceModuleKey.Club, Migration(PersistenceModuleKey.Club, 2, "c2"), Migration(PersistenceModuleKey.Club, 1, "c1")),
        ]);

        catalog.Migrations.Select(m => m.Identity).ShouldBe(["c1", "c2", "r1"]);
    }

    [Fact]
    public void Catalog_rejects_duplicate_identity_and_sequence()
    {
        var club = PersistenceModuleKey.Club;

        Should.Throw<InvalidOperationException>(() => MigrationCatalog.Create(
            [new Contributor(club, Migration(club, 1, "same"), Migration(club, 2, "same"))]));
        Should.Throw<InvalidOperationException>(() => MigrationCatalog.Create(
            [new Contributor(club, Migration(club, 1, "a"), Migration(club, 1, "b"))]));
        Should.Throw<InvalidOperationException>(() => MigrationCatalog.Create(
            [new Contributor(club, Migration(club, 1, "a")), new Contributor(club, Migration(club, 2, "b"))]));
    }

    [Fact]
    public void Catalog_rejects_migrations_for_another_module()
    {
        Should.Throw<InvalidOperationException>(() => MigrationCatalog.Create(
            [new Contributor(PersistenceModuleKey.Club, Migration(PersistenceModuleKey.Registry, 1, "x"))]));
    }

    [Fact]
    public async Task Bootstrap_connection_is_not_available_to_module_services()
    {
        var services = new ServiceCollection();
        services.AddPlatformPersistence(o => o.BootstrapConnectionString = "Host=localhost;Database=x");
        services.AddModulePersistence<ClubContributor>(PersistenceModuleKey.Club);

        services.Where(d => !d.IsKeyedService)
            .Select(d => d.ServiceType.Name + "/" + d.ImplementationType?.Name)
            .ShouldAllBe(name => !name.Contains("Bootstrap"));
        services.Where(d => d.IsKeyedService && d.ServiceKey is not PersistenceModuleKey)
            .ShouldAllBe(d => !d.ServiceKey!.GetType().IsPublic);
        typeof(PersistenceModuleKey).Assembly.GetExportedTypes()
            .ShouldAllBe(t => !t.Name.Contains("Bootstrap"));
        typeof(IModuleConnectionFactory).GetProperties().Select(p => p.Name).ShouldBe(["Module"]);

        await using var provider = services.BuildServiceProvider();
        provider.GetRequiredKeyedService<IModuleConnectionFactory>(PersistenceModuleKey.Club)
            .Module.ShouldBeSameAs(PersistenceModuleKey.Club);
    }

    private static MigrationDescriptor Migration(PersistenceModuleKey module, int sequence, string identity) =>
        new(module, sequence, identity, "select 1;"u8.ToArray());

    private sealed class Contributor(PersistenceModuleKey module, params MigrationDescriptor[] migrations) : IMigrationContributor
    {
        public PersistenceModuleKey Module { get; } = module;

        public IEnumerable<MigrationDescriptor> GetMigrations() => migrations;
    }

    private sealed class ClubContributor : IMigrationContributor
    {
        public PersistenceModuleKey Module => PersistenceModuleKey.Club;

        public IEnumerable<MigrationDescriptor> GetMigrations() => [];
    }
}
