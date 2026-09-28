using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using SocAlytics.Platform.Persistence;
using SocAlytics.Platform.Persistence.Connections;
using Xunit;

namespace SocAlytics.Platform.Persistence.Tests;

public sealed class ConnectionBoundaryTests
{
    [Fact]
    public void BootstrapConnectionTypesAreNotPubliclyAccessible()
    {
        var bootstrapTypes = typeof(PersistenceServiceCollectionExtensions).Assembly
            .GetTypes()
            .Where(type => type.Name.Contains("Bootstrap", StringComparison.Ordinal))
            .ToArray();

        bootstrapTypes.ShouldNotBeEmpty();
        bootstrapTypes.ShouldAllBe(type => !type.IsPublic);
    }

    [Fact]
    public void ModuleConnectionFactoryIsRegisteredAndResolvableForModuleServices()
    {
        var services = new ServiceCollection();
        services.AddPlatformPersistence(ConfigureValidOptions);

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IModuleConnectionFactory>().ShouldNotBeNull();
    }

    [Fact]
    public void ModuleScopedConnectionsUseTheAdoptedSchemaAsTheirSearchPath()
    {
        var services = new ServiceCollection();
        services.AddPlatformPersistence(ConfigureValidOptions);

        using var provider = services.BuildServiceProvider();
        var connectionFactory = provider.GetRequiredService<IModuleConnectionFactory>();

        using var connection = connectionFactory.CreateConnection(ModuleKey.Registry);

        connection.ConnectionString.ShouldContain("Search Path=registry");
    }

    [Fact]
    public void MissingBootstrapConnectionStringIsRejected()
    {
        var services = new ServiceCollection();

        Should.Throw<InvalidOperationException>(() => services.AddPlatformPersistence(options =>
        {
            options.RuntimeConnectionString = "Host=localhost;Database=socalytics;Username=runtime";
        }));
    }

    [Fact]
    public void MissingRuntimeConnectionStringIsRejected()
    {
        var services = new ServiceCollection();

        Should.Throw<InvalidOperationException>(() => services.AddPlatformPersistence(options =>
        {
            options.BootstrapConnectionString = "Host=localhost;Database=socalytics;Username=bootstrap";
        }));
    }

    private static void ConfigureValidOptions(PersistenceOptions options)
    {
        options.BootstrapConnectionString = "Host=localhost;Database=socalytics;Username=bootstrap";
        options.RuntimeConnectionString = "Host=localhost;Database=socalytics;Username=runtime";
    }
}
