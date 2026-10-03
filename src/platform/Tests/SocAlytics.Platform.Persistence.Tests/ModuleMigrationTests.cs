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

public sealed class ModuleMigrationTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine").Build();

    public async ValueTask InitializeAsync() => await _container.StartAsync();

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    [Fact]
    public async Task ModuleContributorsCreateOnlyTheSixSchemasAndMigrationJournal()
    {
        var name = "db_" + Guid.NewGuid().ToString("N");
        await using (var admin = new NpgsqlConnection(_container.GetConnectionString()))
        {
            await admin.OpenAsync(TestContext.Current.CancellationToken);
            await using var create = new NpgsqlCommand($"CREATE DATABASE {name}", admin);
            await create.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var connectionString = new NpgsqlConnectionStringBuilder(_container.GetConnectionString()) { Database = name }.ConnectionString;
        var services = new ServiceCollection();
        services.AddPlatformPersistence(o => o.ConnectionString = connectionString);
        services.AddClubModule();
        services.AddIdentityAccessModule();
        services.AddRecordingsModule();
        services.AddRegistryModule();
        services.AddAnalysisModule();
        services.AddAgentOrchestrationModule();
        await using var provider = services.BuildServiceProvider();

        var runner = provider.GetRequiredService<MigrationRunner>();
        await runner.RunAsync(TestContext.Current.CancellationToken);
        await runner.RunAsync(TestContext.Current.CancellationToken);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        var schemas = await QueryAsync(connection,
            "SELECT nspname FROM pg_namespace WHERE nspname NOT LIKE 'pg\\_%' AND nspname <> 'information_schema' AND nspname <> 'public' ORDER BY 1");
        schemas.ShouldBe(ModuleKey.All.Select(m => m.Schema).Append("socalytics_migrations").Order(StringComparer.Ordinal).ToList());

        var tables = await QueryAsync(connection,
            "SELECT table_schema || '.' || table_name FROM information_schema.tables WHERE table_schema NOT IN ('pg_catalog', 'information_schema') ORDER BY 1");
        tables.ShouldBe(["socalytics_migrations.history"]);

        var history = await QueryAsync(connection, "SELECT module || '/' || sequence FROM socalytics_migrations.history ORDER BY 1");
        history.ShouldBe(ModuleKey.All.Select(m => m.Name + "/1").Order(StringComparer.Ordinal).ToList());
    }

    private static async Task<List<string>> QueryAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        var rows = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            rows.Add(reader.GetString(0));
        }

        return rows;
    }
}
