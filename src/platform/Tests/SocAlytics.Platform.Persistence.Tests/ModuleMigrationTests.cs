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

    [Fact]
    public async Task Catalog_shows_history_fields_runtime_privilege_denial_and_no_club_id()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        var services = new ServiceCollection();
        services.AddSingleton(NpgsqlDataSource.Create(connectionString));
        services.AddClubModule().AddIdentityAccessModule().AddRecordingsModule()
            .AddRegistryModule().AddAnalysisModule().AddAgentOrchestrationModule();

        await using var provider = services.BuildServiceProvider();
        await provider.GetRequiredService<IMigrationRunner>().RunAsync();

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        var columns = await ReadAsync(connection,
            "SELECT attname || ':' || format_type(atttypid, atttypmod) || ':' || attnotnull FROM pg_attribute " +
            "WHERE attrelid = 'socalytics_migrations.history'::regclass AND attnum > 0 AND NOT attisdropped ORDER BY attnum");
        columns.ShouldBe(
        [
            "module:text:true", "sequence:integer:true", "script_identity:text:true", "checksum:text:true",
            "applied_at:timestamp with time zone:true",
        ]);

        const string productSchemas = "n.nspname NOT LIKE 'pg\\_%' AND n.nspname <> 'information_schema'";
        var clubIdNames = await ReadAsync(connection,
            $"SELECT 'schema:' || n.nspname FROM pg_namespace n WHERE {productSchemas} AND n.nspname ILIKE '%club\\_id%' " +
            $"UNION ALL SELECT 'relation:' || n.nspname || '.' || c.relname FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace WHERE {productSchemas} AND c.relname ILIKE '%club\\_id%' " +
            $"UNION ALL SELECT 'column:' || n.nspname || '.' || c.relname || '.' || a.attname FROM pg_attribute a JOIN pg_class c ON c.oid = a.attrelid JOIN pg_namespace n ON n.oid = c.relnamespace WHERE {productSchemas} AND a.attnum > 0 AND NOT a.attisdropped AND a.attname ILIKE '%club\\_id%' " +
            $"UNION ALL SELECT 'function:' || n.nspname || '.' || p.proname FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace WHERE {productSchemas} AND (p.proname ILIKE '%club\\_id%' OR EXISTS (SELECT 1 FROM unnest(p.proargnames) AS arg WHERE arg ILIKE '%club\\_id%'))");
        clubIdNames.ShouldBeEmpty();

        foreach (var module in PersistenceModule.All)
        {
            var runtime = module.Key + "_runtime";
            (await ScalarAsync(connection, $"SELECT has_schema_privilege('{runtime}', '{module.Key}', 'USAGE')::text")).ShouldBe("true");
            (await ScalarAsync(connection, $"SELECT has_schema_privilege('{runtime}', '{module.Key}', 'CREATE')::text")).ShouldBe("false");
            (await ScalarAsync(connection, $"SELECT has_schema_privilege('{runtime}', 'socalytics_migrations', 'USAGE')::text")).ShouldBe("false");
            (await ScalarAsync(connection, $"SELECT has_table_privilege('{runtime}', 'socalytics_migrations.history', 'SELECT')::text")).ShouldBe("false");
            (await ScalarAsync(connection, $"SELECT has_database_privilege('{runtime}', current_database(), 'CREATE')::text")).ShouldBe("false");

            foreach (var peer in PersistenceModule.All.Where(m => m != module))
            {
                (await ScalarAsync(connection, $"SELECT has_schema_privilege('{runtime}', '{peer.Key}', 'USAGE')::text")).ShouldBe("false");
            }
        }

        var factory = provider.GetRequiredKeyedService<IModuleConnectionFactory>(PersistenceModule.Club.Key);
        await using var session = await factory.OpenConnectionAsync();
        foreach (var sql in new[] { "SELECT * FROM socalytics_migrations.history", "DELETE FROM socalytics_migrations.history", "CREATE TABLE club.t (n int)" })
        {
            await using var command = new NpgsqlCommand(sql, session);
            (await Should.ThrowAsync<PostgresException>(() => command.ExecuteNonQueryAsync())).SqlState.ShouldBe("42501");
        }
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
