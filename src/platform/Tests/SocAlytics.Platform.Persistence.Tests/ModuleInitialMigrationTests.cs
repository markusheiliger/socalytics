using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using SocAlytics.Platform.AgentOrchestration;
using SocAlytics.Platform.Analysis;
using SocAlytics.Platform.Club;
using SocAlytics.Platform.IdentityAccess;
using SocAlytics.Platform.Persistence;
using SocAlytics.Platform.Recordings;
using SocAlytics.Platform.Registry;
using Testcontainers.PostgreSql;
using Xunit;

namespace SocAlytics.Platform.Persistence.Tests;

public sealed class ModuleInitialMigrationTests
{
    [Fact]
    public async Task StartupCreatesModuleSchemasAndRunsModuleGrantsWithoutDomainTables()
    {
        await using var database = new PostgreSqlBuilder("postgres:17-alpine").Build();
        await database.StartAsync(TestContext.Current.CancellationToken);

        var services = new ServiceCollection();
        services.AddPlatformPersistence(options =>
        {
            options.BootstrapConnectionString = database.GetConnectionString();
            options.RuntimeConnectionString = database.GetConnectionString();
        });
        services
            .AddClubModule()
            .AddIdentityAccessModule()
            .AddRecordingsModule()
            .AddRegistryModule()
            .AddAnalysisModule()
            .AddAgentOrchestrationModule();
        using var provider = services.BuildServiceProvider();
        var contributors = provider.GetServices<IModuleMigrationContributor>().ToArray();

        contributors.Select(contributor => contributor.ModuleKey).ShouldBe(Enum.GetValues<ModuleKey>());
        provider.GetRequiredService<MigrationOrchestrator>().Run();

        await using var connection = new NpgsqlConnection(database.GetConnectionString());
        var schemas = Enum.GetValues<ModuleKey>().Select(module => module.ToSchemaName()).ToArray();
        (await connection.QueryAsync<string>(
            "SELECT schema_name FROM information_schema.schemata WHERE schema_name = ANY(@schemas) ORDER BY schema_name",
            new { schemas })).Order().ShouldBe(schemas.Order());
        (await connection.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'socalytics_migrations' AND table_name = 'history'"))
            .ShouldBe(1);
        (await connection.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM socalytics_migrations.history")).ShouldBe(6);
        (await connection.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM information_schema.tables WHERE table_schema = ANY(@schemas) AND table_type = 'BASE TABLE'",
            new { schemas })).ShouldBe(0);
    }
}
