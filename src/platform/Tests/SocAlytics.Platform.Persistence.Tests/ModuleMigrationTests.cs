#pragma warning disable xUnit1051 // Tests are short-lived; cancellation is not exercised here.
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using SocAlytics.Platform.AgentOrchestration;
using SocAlytics.Platform.Analysis;
using SocAlytics.Platform.Club;
using SocAlytics.Platform.IdentityAccess;
using SocAlytics.Platform.Recordings;
using SocAlytics.Platform.Registry;
using Xunit;

namespace SocAlytics.Platform.Persistence.Tests;

public sealed class ModuleMigrationTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task Module_initial_migrations_create_only_owned_schemas_and_no_domain_tables()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        var services = new ServiceCollection();
        services.AddSingleton(NpgsqlDataSource.Create(connectionString));
        services.AddClubModule().AddIdentityAccessModule().AddRecordingsModule()
            .AddRegistryModule().AddAnalysisModule().AddAgentOrchestrationModule();

        await using var provider = services.BuildServiceProvider();
        await provider.GetRequiredService<IMigrationRunner>().RunAsync();
        await provider.GetRequiredService<IMigrationRunner>().RunAsync();

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        var schemas = await ReadAsync(connection,
            "SELECT nspname || ':' || pg_get_userbyid(nspowner) FROM pg_namespace WHERE nspname NOT LIKE 'pg\\_%' AND nspname NOT IN ('information_schema','public') ORDER BY 1");
        schemas.ShouldBe(
        [
            "agent_orchestration:agent_orchestration_owner", "analysis:analysis_owner", "club:club_owner",
            "identity_access:identity_access_owner", "recordings:recordings_owner", "registry:registry_owner",
            "socalytics_migrations:" + await ScalarAsync(connection, "SELECT current_user"),
        ]);

        var tables = await ReadAsync(connection,
            "SELECT schemaname || '.' || tablename FROM pg_tables WHERE schemaname NOT IN ('pg_catalog','information_schema','public')");
        tables.ShouldBe(["socalytics_migrations.history"]);

        var history = await ReadAsync(connection, "SELECT module || ':' || sequence FROM socalytics_migrations.history ORDER BY 1");
        history.ShouldBe(PersistenceModule.All.Select(m => m.Key + ":1").Order().ToArray());
    }

    private static async Task<string> ScalarAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<string[]> ReadAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<string>();
        while (await reader.ReadAsync())
        {
            rows.Add(reader.GetString(0));
        }

        return [.. rows];
    }
}
