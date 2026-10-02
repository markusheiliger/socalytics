using System.Reflection;
using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using SocAlytics.Platform.Persistence;
using Xunit;

namespace SocAlytics.Platform.Persistence.Tests;

public sealed class PersistenceFoundationTests
{
    private const string ResourceName = "SocAlytics.Platform.Persistence.Tests.sample.sql";

    [Fact]
    public void Adopted_module_keys_are_fixed()
    {
        PersistenceModuleIdentity.All.Select(module => module.Key).ShouldBe(
        [
            "club",
            "identity_access",
            "recordings",
            "registry",
            "analysis",
            "agent_orchestration"
        ]);
    }

    [Fact]
    public void Embedded_migration_checksum_is_sha256_of_resource_bytes()
    {
        var descriptor = CreateMigration(PersistenceModuleIdentity.Club, 1, "initial");
        using var resource = typeof(PersistenceFoundationTests).Assembly.GetManifestResourceStream(ResourceName)!;
        using var content = new MemoryStream();
        resource.CopyTo(content);
        var expected = Convert.ToHexString(SHA256.HashData(content.ToArray())).ToLowerInvariant();

        descriptor.Checksum.ShouldBe(expected);
    }

    [Fact]
    public void Migration_catalog_rejects_duplicate_module_identity()
    {
        var first = CreateMigration(PersistenceModuleIdentity.Club, 1, "initial");
        var duplicate = CreateMigration(PersistenceModuleIdentity.Club, 2, "initial");

        Should.Throw<ArgumentException>(() => new MigrationCatalog([first, duplicate]));
    }

    [Fact]
    public void Migration_catalog_rejects_duplicate_module_sequence()
    {
        var first = CreateMigration(PersistenceModuleIdentity.Club, 1, "initial");
        var duplicate = CreateMigration(PersistenceModuleIdentity.Club, 1, "next");

        Should.Throw<ArgumentException>(() => new MigrationCatalog([first, duplicate]));
    }

    [Fact]
    public void Bootstrap_connection_is_not_part_of_the_public_module_service_surface()
    {
        var services = new ServiceCollection();
        services.AddPlatformPersistence(
            "Host=localhost;Database=runtime;Username=runtime",
            "Host=localhost;Database=bootstrap;Username=bootstrap");
        services.AddModuleDatabaseConnections(PersistenceModuleIdentity.Club);

        var persistenceContracts = services
            .Where(descriptor => descriptor.ServiceType.Assembly == typeof(IRuntimeDatabaseConnectionFactory).Assembly)
            .Select(descriptor => descriptor.ServiceType)
            .ToArray();

        persistenceContracts.ShouldContain(typeof(IRuntimeDatabaseConnectionFactory));
        persistenceContracts.Where(type => type.Name.Contains("Bootstrap", StringComparison.Ordinal))
            .ShouldAllBe(type => !type.IsPublic);
        typeof(IRuntimeDatabaseConnectionFactory).Assembly.GetExportedTypes()
            .ShouldNotContain(type => type.Name.Contains("Bootstrap", StringComparison.Ordinal));
        var runtimeFactory = services.Single(descriptor =>
            descriptor.ServiceType == typeof(IRuntimeDatabaseConnectionFactory));
        runtimeFactory.IsKeyedService.ShouldBeTrue();
        runtimeFactory.ServiceKey.ShouldBe(PersistenceModuleIdentity.Club.Key);
        runtimeFactory.Lifetime.ShouldBe(ServiceLifetime.Singleton);
    }

    private static MigrationDescriptor CreateMigration(
        PersistenceModuleIdentity module,
        int sequence,
        string identity)
    {
        return MigrationDescriptor.FromEmbeddedResource(
            module,
            sequence,
            identity,
            Assembly.GetExecutingAssembly(),
            ResourceName);
    }
}
