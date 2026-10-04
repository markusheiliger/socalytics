using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using SocAlytics.Platform.AgentOrchestration;
using SocAlytics.Platform.Analysis;
using SocAlytics.Platform.Club;
using SocAlytics.Platform.IdentityAccess;
using SocAlytics.Platform.Recordings;
using SocAlytics.Platform.Registry;
using Testcontainers.PostgreSql;
using Xunit;

namespace SocAlytics.Platform.Persistence.Tests;

public sealed class ModuleInitialMigrationTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine").Build();

    public ValueTask InitializeAsync() => new(_container.StartAsync(TestContext.Current.CancellationToken));

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    [Fact]
    public async Task InitialMigrationsCreateAllSixSchemasWithoutDomainTables()
    {
        var services = new ServiceCollection().AddPlatformPersistence(_container.GetConnectionString());
        services.AddClubModule()
            .AddIdentityAccessModule()
            .AddRecordingsModule()
            .AddRegistryModule()
            .AddAnalysisModule()
            .AddAgentOrchestrationModule();

        await using var provider = services.BuildServiceProvider();
        var orchestrator = provider.GetRequiredService<MigrationOrchestrator>();
        await orchestrator.MigrateAsync(TestContext.Current.CancellationToken);
        await orchestrator.MigrateAsync(TestContext.Current.CancellationToken);

        await using var connection = new NpgsqlConnection(_container.GetConnectionString());
        var schemas = (await connection.QueryAsync<string>(
            "SELECT nspname FROM pg_namespace WHERE nspname = 'socalytics_migrations' OR nspname = ANY(@modules) ORDER BY nspname COLLATE \"C\"",
            new { modules = PersistenceModuleKey.All.Select(m => m.Schema).ToArray() })).ToArray();
        schemas.ShouldBe(
            [.. PersistenceModuleKey.All.Select(m => m.Schema).Append("socalytics_migrations").Order(StringComparer.Ordinal)]);

        (await connection.QueryAsync<string>(
            "SELECT n.nspname || '.' || c.relname FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace " +
            "WHERE c.relkind IN ('r','p','v','m','f') AND n.nspname = ANY(@modules)",
            new { modules = PersistenceModuleKey.All.Select(m => m.Schema).ToArray() })).ShouldBeEmpty();

        (await connection.QueryAsync<string>(
            "SELECT module || '/' || identity FROM socalytics_migrations.history ORDER BY module"))
            .ShouldBe(PersistenceModuleKey.All.Select(m => m.Key + "/0001_initial").Order(StringComparer.Ordinal));
    }
}
