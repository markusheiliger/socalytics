using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using SocAlytics.Platform.Infrastructure;
using SocAlytics.Platform.Infrastructure.Persistence;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Infrastructure;

public sealed class PlatformDataSourceTests(PostgresContainerFixture postgres)
{
    [Fact]
    public async Task ResolvedDataSourceConnectsAsAppRole()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var db = await postgres.CreateDatabaseAsync(cancellationToken);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"ConnectionStrings:{DatabaseConnectionNames.Runtime}"] = db.AppConnectionString,
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddInfrastructure();
        await using var provider = services.BuildServiceProvider();

        var source = provider.GetRequiredService<PlatformDataSource>();
        source.TryGetDataSource(out var dataSource).ShouldBeTrue();

        await using var connection = await dataSource!.OpenConnectionAsync(cancellationToken);
        var user = await connection.ExecuteScalarAsync<string>("SELECT current_user");
        user.ShouldBe("socalytics_app");
    }

    [Fact]
    public async Task MissingConnectionStringYieldsNoDataSourceWithoutThrowing()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddInfrastructure();
        await using var provider = services.BuildServiceProvider();

        var source = provider.GetRequiredService<PlatformDataSource>();

        source.TryGetDataSource(out var dataSource).ShouldBeFalse();
        dataSource.ShouldBeNull();
    }
}
