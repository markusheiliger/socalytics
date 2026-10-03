using Xunit;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace SocAlytics.Platform.Persistence.Tests;

public sealed class BootstrapBoundaryTests
{
    private sealed class Marker;

    [Fact]
    public void BootstrapTypesAreNotPublic()
    {
        var publicTypes = typeof(ModuleKey).Assembly.GetExportedTypes();
        publicTypes.ShouldNotContain(t => t.Name.Contains("DataSource", StringComparison.Ordinal));
        publicTypes.ShouldNotContain(typeof(PersistenceOptions).Assembly.GetType("SocAlytics.Platform.Persistence.PersistenceDataSource")!);
    }

    [Fact]
    public void PublicSurfaceExposesNoRawConnectionString()
    {
        new PersistenceOptions { ConnectionString = "Host=h;Password=secret" }.ToString().ShouldNotContain("secret");
    }

    [Fact]
    public void ModulesOnlyReceiveRoleScopedFactoryNotBootstrapConnection()
    {
        var services = new ServiceCollection();
        services.AddPlatformPersistence(o => o.ConnectionString = "Host=localhost");
        services.AddModulePersistence<Marker>(new EmptyContributor());

        var registered = services.Select(d => d.ServiceType).ToArray();
        registered.ShouldContain(typeof(IModuleConnectionFactory<Marker>));
        registered.ShouldNotContain(typeof(Npgsql.NpgsqlDataSource));
        registered.ShouldNotContain(typeof(Npgsql.NpgsqlConnection));
        services.Where(d => d.ServiceType.IsPublic && d.ServiceType.Namespace == "SocAlytics.Platform.Persistence")
            .Select(d => d.ServiceType)
            .ShouldBe([typeof(PersistenceOptions), typeof(IModuleMigrationContributor), typeof(IModuleConnectionFactory<Marker>)], ignoreOrder: true);
    }

    private sealed class EmptyContributor : IModuleMigrationContributor
    {
        public ModuleKey Module => ModuleKey.Club;
        public IReadOnlyList<MigrationDescriptor> Migrations => [];
    }
}
