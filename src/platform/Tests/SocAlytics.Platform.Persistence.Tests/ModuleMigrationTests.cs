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
    private static readonly string[] Schemas =
        ["club", "identity_access", "recordings", "registry", "analysis", "agent_orchestration"];

    private static ServiceCollection Compose(string connectionString)
    {
        var services = new ServiceCollection();
        services.AddPlatformPersistence(o => o.BootstrapConnectionString = connectionString);
        services
            .AddClubModule()
            .AddIdentityAccessModule()
            .AddRecordingsModule()
            .AddRegistryModule()
            .AddAnalysisModule()
            .AddAgentOrchestrationModule();
        return services;
    }

    private static async Task<List<string>> QueryAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        var values = new List<string>();
        while (await reader.ReadAsync())
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }

    [Fact]
    public async Task InitialMigrationsCreateOnlyTheSixSchemasAndHistory()
    {
        var cs = await postgres.CreateDatabaseAsync();
        await using var provider = Compose(cs).BuildServiceProvider();

        await provider.GetRequiredService<IMigrationRunner>().MigrateAsync();

        (await QueryAsync(cs,
            "SELECT nspname FROM pg_namespace WHERE nspname = ANY(@s) OR nspname = 'socalytics_migrations' ORDER BY nspname"
                .Replace("@s", "ARRAY['club','identity_access','recordings','registry','analysis','agent_orchestration']")))
            .ShouldBe(Schemas.Append("socalytics_migrations").Order().ToArray());
        (await QueryAsync(cs,
            "SELECT module_key || '/' || sequence FROM socalytics_migrations.history ORDER BY module_key"))
            .ShouldBe(Schemas.Order().Select(s => $"{s}/1").ToArray());
        (await QueryAsync(cs,
            "SELECT schemaname || '.' || tablename FROM pg_tables WHERE schemaname = ANY(ARRAY['club','identity_access','recordings','registry','analysis','agent_orchestration'])"))
            .ShouldBeEmpty();
        (await QueryAsync(cs,
            "SELECT nspname || ':' || nspowner::regrole::text FROM pg_namespace WHERE nspname = ANY(ARRAY['club','identity_access','recordings','registry','analysis','agent_orchestration']) ORDER BY nspname"))
            .ShouldBe(Schemas.Order().Select(s => $"{s}:{s}_owner").ToArray());
    }

    [Fact]
    public async Task RepeatRunsAndRepeatCompositionAreIdempotent()
    {
        var cs = await postgres.CreateDatabaseAsync();
        var services = Compose(cs);
        services.AddClubModule();
        await using var provider = services.BuildServiceProvider();
        var runner = provider.GetRequiredService<IMigrationRunner>();

        await runner.MigrateAsync();
        await runner.MigrateAsync();

        (await QueryAsync(cs, "SELECT count(*)::text FROM socalytics_migrations.history")).ShouldBe(["6"]);
    }
}
