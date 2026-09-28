using System.Reflection;
using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using SocAlytics.Platform.Persistence;
using Xunit;

namespace SocAlytics.Platform.Persistence.Tests;

public sealed class PersistenceFoundationTests
{
    [Fact]
    public void Module_keys_are_fixed_in_adopted_order()
    {
        ModuleIdentityCatalog.Ordered.Select(ModuleIdentityCatalog.GetKey).ShouldBe(
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
    public void Migration_descriptor_uses_embedded_resource_content_for_checksum()
    {
        var descriptor = MigrationDescriptor.FromEmbeddedResource(
            ModuleIdentity.Club,
            1,
            "initial",
            Assembly.GetExecutingAssembly(),
            "SocAlytics.Platform.Persistence.Tests.Resources.Initial.sql");

        descriptor.Checksum.ToArray().ShouldBe(SHA256.HashData(descriptor.Content.Span));
    }

    [Fact]
    public void Duplicate_migration_identity_or_sequence_is_rejected_per_module()
    {
        var registry = new MigrationDescriptorRegistry();
        var assembly = Assembly.GetExecutingAssembly();
        var first = MigrationDescriptor.FromEmbeddedResource(
            ModuleIdentity.Club,
            1,
            "initial",
            assembly,
            "SocAlytics.Platform.Persistence.Tests.Resources.Initial.sql");

        registry.Register(first);

        Should.Throw<ArgumentException>(() => registry.Register(MigrationDescriptor.FromEmbeddedResource(
            ModuleIdentity.Club,
            2,
            "initial",
            assembly,
            "SocAlytics.Platform.Persistence.Tests.Resources.Initial.sql")));
        Should.Throw<ArgumentException>(() => registry.Register(MigrationDescriptor.FromEmbeddedResource(
            ModuleIdentity.Club,
            1,
            "second",
            assembly,
            "SocAlytics.Platform.Persistence.Tests.Resources.Initial.sql")));
    }

    [Fact]
    public void Bootstrap_connection_is_not_registered_for_module_services()
    {
        IServiceCollection services = new ServiceCollection();

        services.AddPlatformPersistence(
            "Host=runtime;Database=platform;Username=runtime;******",
            "Host=bootstrap;Database=platform;Username=bootstrap;******");

        services.ShouldNotContain(descriptor => descriptor.ServiceType == typeof(BootstrapConnectionFactory));
        services.ShouldContain(descriptor => descriptor.ServiceType == typeof(IModuleConnectionFactory));
    }
}
