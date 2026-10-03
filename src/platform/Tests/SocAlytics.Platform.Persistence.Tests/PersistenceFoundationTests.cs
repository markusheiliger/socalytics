using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using Xunit;

namespace SocAlytics.Platform.Persistence.Tests;

public sealed class PersistenceFoundationTests
{
    [Fact]
    public void Module_keys_are_the_fixed_adopted_set_in_order()
    {
        PersistenceModule.All.Select(m => m.Key).ShouldBe(
            ["club", "identity_access", "recordings", "registry", "analysis", "agent_orchestration"]);
        typeof(PersistenceModule).GetConstructors().ShouldBeEmpty();
    }

    [Fact]
    public void Checksum_is_sha256_of_the_exact_bytes()
    {
        var descriptor = Descriptor(PersistenceModule.Club, 1, "a", "abc");

        descriptor.Checksum.ShouldBe("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");
        Descriptor(PersistenceModule.Club, 1, "a", "abd").Checksum.ShouldNotBe(descriptor.Checksum);
    }

    [Fact]
    public void Embedded_resource_checksum_matches_content()
    {
        var descriptor = MigrationDescriptor.FromEmbeddedResource(
            PersistenceModule.Club, 1, "sample", typeof(PersistenceFoundationTests).Assembly,
            "SocAlytics.Platform.Persistence.Tests.Sample.sql");

        descriptor.Checksum.ShouldBe(Descriptor(PersistenceModule.Club, 1, "sample", descriptor.Script).Checksum);
    }

    [Fact]
    public void Catalog_orders_by_module_then_sequence()
    {
        var catalog = MigrationCatalog.Create(
        [
            new Contributor(PersistenceModule.Registry, Descriptor(PersistenceModule.Registry, 1, "r1", "x")),
            new Contributor(PersistenceModule.Club,
                Descriptor(PersistenceModule.Club, 2, "c2", "x"), Descriptor(PersistenceModule.Club, 1, "c1", "x")),
        ]);

        catalog.Migrations.Select(m => m.Identity).ShouldBe(["c1", "c2", "r1"]);
    }

    [Fact]
    public void Catalog_rejects_duplicate_sequences_identities_and_foreign_modules()
    {
        Should.Throw<InvalidOperationException>(() => MigrationCatalog.Create(
            [new Contributor(PersistenceModule.Club, Descriptor(PersistenceModule.Club, 1, "a", "x"), Descriptor(PersistenceModule.Club, 1, "b", "x"))]));
        Should.Throw<InvalidOperationException>(() => MigrationCatalog.Create(
            [new Contributor(PersistenceModule.Club, Descriptor(PersistenceModule.Club, 1, "a", "x"), Descriptor(PersistenceModule.Club, 2, "a", "x"))]));
        Should.Throw<InvalidOperationException>(() => MigrationCatalog.Create(
            [new Contributor(PersistenceModule.Club, Descriptor(PersistenceModule.Registry, 1, "a", "x"))]));
        Should.Throw<InvalidOperationException>(() => MigrationCatalog.Create(
            [new Contributor(PersistenceModule.Club), new Contributor(PersistenceModule.Club)]));
    }

    [Fact]
    public void Descriptor_rejects_non_positive_sequence_and_blank_identity()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => Descriptor(PersistenceModule.Club, 0, "a", "x"));
        Should.Throw<ArgumentException>(() => Descriptor(PersistenceModule.Club, 1, " ", "x"));
    }

    [Fact]
    public void Contributor_for_a_different_module_is_rejected()
    {
        Should.Throw<ArgumentException>(() => new ServiceCollection()
            .AddModulePersistence(PersistenceModule.Registry, new Contributor(PersistenceModule.Club)));
    }

    [Fact]
    public void Bootstrap_connection_is_not_exposed_to_module_services()
    {
        var assembly = typeof(PersistenceModule).Assembly;
        assembly.GetExportedTypes().Where(t => t.Name.Contains("Bootstrap", StringComparison.Ordinal)).ShouldBeEmpty();

        var services = new ServiceCollection();
        services.AddModulePersistence(PersistenceModule.Club, new Contributor(PersistenceModule.Club));

        services.Where(d => d.ServiceType.IsPublic || d.ServiceType.IsNestedPublic || d.IsKeyedService)
            .Select(d => d.ServiceType)
            .ShouldAllBe(t => !t.Name.Contains("Bootstrap", StringComparison.Ordinal));

        var factory = services.Single(d => d.ServiceType == typeof(IModuleConnectionFactory));
        factory.ServiceKey.ShouldBe("club");
        typeof(IModuleConnectionFactory).GetMembers().Select(m => m.Name)
            .ShouldNotContain(n => n.Contains("Bootstrap", StringComparison.Ordinal));
        typeof(NpgsqlDataSource).ShouldNotBeNull();
    }

    private static MigrationDescriptor Descriptor(PersistenceModule module, int sequence, string identity, string script) =>
        new(module, sequence, identity, System.Text.Encoding.UTF8.GetBytes(script));

    private sealed class Contributor(PersistenceModule module, params MigrationDescriptor[] migrations) : IMigrationContributor
    {
        public PersistenceModule Module { get; } = module;

        public IEnumerable<MigrationDescriptor> GetMigrations() => migrations;
    }
}
