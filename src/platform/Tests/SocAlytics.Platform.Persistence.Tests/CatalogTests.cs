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

/// <summary>Catalog-level checks against a disposable PostgreSQL container migrated by the real module contributors.</summary>
public sealed class CatalogTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private const string NonSystemSchemas =
        "nspname NOT LIKE 'pg\\_%' AND nspname <> 'information_schema' AND nspname <> 'public'";

    private async Task<string> MigratedDatabaseAsync()
    {
        var cs = await postgres.CreateDatabaseAsync();
        var services = new ServiceCollection();
        services.AddPlatformPersistence(o => o.BootstrapConnectionString = cs);
        services
            .AddClubModule()
            .AddIdentityAccessModule()
            .AddRecordingsModule()
            .AddRegistryModule()
            .AddAnalysisModule()
            .AddAgentOrchestrationModule();
        await using var provider = services.BuildServiceProvider();
        await provider.GetRequiredService<IMigrationRunner>().MigrateAsync();
        return cs;
    }

    private static async Task<List<string>> QueryAsync(string cs, string sql)
    {
        await using var connection = new NpgsqlConnection(cs);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        var values = new List<string>();
        while (await reader.ReadAsync())
        {
            values.Add(reader.GetValue(0).ToString()!);
        }

        return values;
    }

    [Fact]
    public async Task OnlyTheAdoptedSchemasExistAndEachModuleSchemaIsOwnedByItsOwnerRole()
    {
        var cs = await MigratedDatabaseAsync();

        (await QueryAsync(cs, $"SELECT nspname FROM pg_namespace WHERE {NonSystemSchemas} ORDER BY nspname"))
            .ShouldBe(PersistenceModuleKey.All.Select(m => m.SchemaName).Append("socalytics_migrations").Order().ToArray());

        foreach (var m in PersistenceModuleKey.All)
        {
            (await QueryAsync(cs, $"SELECT nspowner::regrole::text FROM pg_namespace WHERE nspname = '{m.SchemaName}'"))
                .ShouldBe([m.OwnerRoleName]);
        }

        var owners = PersistenceModuleKey.All.Select(m => $"'{m.OwnerRoleName}'");
        (await QueryAsync(cs,
            $"SELECT nspname FROM pg_namespace WHERE nspname = 'socalytics_migrations' AND nspowner::regrole::text IN ({string.Join(',', owners)})"))
            .ShouldBeEmpty();
    }

    [Fact]
    public async Task MigrationHistoryHasTheDocumentedFieldsAndRecordedValues()
    {
        var cs = await MigratedDatabaseAsync();

        (await QueryAsync(cs,
            "SELECT column_name || ':' || data_type || ':' || is_nullable FROM information_schema.columns " +
            "WHERE table_schema = 'socalytics_migrations' AND table_name = 'history' ORDER BY ordinal_position"))
            .ShouldBe(
            [
                "module_key:text:NO",
                "sequence:integer:NO",
                "script_name:text:NO",
                "checksum:character:NO",
                "applied_at:timestamp with time zone:NO",
            ]);
        (await QueryAsync(cs,
            "SELECT character_maximum_length::text FROM information_schema.columns " +
            "WHERE table_schema = 'socalytics_migrations' AND table_name = 'history' AND column_name = 'checksum'"))
            .ShouldBe(["64"]);
        (await QueryAsync(cs,
            "SELECT count(*)::text FROM socalytics_migrations.history WHERE checksum ~ '^[0-9a-f]{64}$' AND sequence = 1 AND applied_at IS NOT NULL"))
            .ShouldBe(["6"]);
        (await QueryAsync(cs,
            "SELECT conname FROM pg_constraint WHERE conrelid = 'socalytics_migrations.history'::regclass AND contype = 'u' ORDER BY conname"))
            .ShouldBe(["history_module_script_key", "history_module_sequence_key"]);
        (await QueryAsync(cs,
            "SELECT string_agg(module_key, ',' ORDER BY module_key) FROM socalytics_migrations.history"))
            .ShouldBe([string.Join(',', PersistenceModuleKey.All.Select(m => m.Name).Order())]);
    }

    [Fact]
    public async Task RuntimeRolesHoldOnlyUsageOnTheirOwnSchema()
    {
        var cs = await MigratedDatabaseAsync();

        foreach (var m in PersistenceModuleKey.All)
        {
            (await QueryAsync(cs, $"SELECT has_schema_privilege('{m.RuntimeRoleName}', '{m.SchemaName}', 'USAGE')::text"))
                .ShouldBe(["True"]);
            (await QueryAsync(cs, $"SELECT has_schema_privilege('{m.RuntimeRoleName}', '{m.SchemaName}', 'CREATE')::text"))
                .ShouldBe(["False"]);
            (await QueryAsync(cs, $"SELECT has_schema_privilege('{m.RuntimeRoleName}', 'socalytics_migrations', 'USAGE')::text"))
                .ShouldBe(["False"]);
            (await QueryAsync(cs, $"SELECT has_table_privilege('{m.RuntimeRoleName}', 'socalytics_migrations.history', 'SELECT')::text"))
                .ShouldBe(["False"]);
            (await QueryAsync(cs, $"SELECT has_schema_privilege('{m.RuntimeRoleName}', 'public', 'CREATE')::text"))
                .ShouldBe(["False"]);
            (await QueryAsync(cs, $"SELECT has_database_privilege('{m.RuntimeRoleName}', current_database(), 'CREATE')::text"))
                .ShouldBe(["False"]);

            foreach (var peer in PersistenceModuleKey.All.Where(k => k != m))
            {
                (await QueryAsync(cs,
                    $"SELECT (has_schema_privilege('{m.RuntimeRoleName}', '{peer.SchemaName}', 'USAGE') OR has_schema_privilege('{m.RuntimeRoleName}', '{peer.SchemaName}', 'CREATE'))::text"))
                    .ShouldBe(["False"]);
                (await QueryAsync(cs, $"SELECT pg_has_role('{m.RuntimeRoleName}', '{peer.OwnerRoleName}', 'USAGE')::text"))
                    .ShouldBe(["False"]);
            }

            (await QueryAsync(cs, $"SELECT pg_has_role('{m.RuntimeRoleName}', '{m.OwnerRoleName}', 'USAGE')::text"))
                .ShouldBe(["False"]);
        }
    }

    [Fact]
    public async Task NoClubIdExistsInAnySchemaTableViewColumnOrFunctionArgument()
    {
        var cs = await MigratedDatabaseAsync();

        (await QueryAsync(cs, $"SELECT nspname FROM pg_namespace WHERE {NonSystemSchemas} AND nspname ILIKE '%club\\_id%'"))
            .ShouldBeEmpty();
        (await QueryAsync(cs,
            "SELECT n.nspname || '.' || c.relname FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace " +
            $"WHERE {NonSystemSchemas.Replace("nspname", "n.nspname")} AND c.relname ILIKE '%club\\_id%'"))
            .ShouldBeEmpty();
        (await QueryAsync(cs,
            "SELECT n.nspname || '.' || c.relname || '.' || a.attname FROM pg_attribute a " +
            "JOIN pg_class c ON c.oid = a.attrelid JOIN pg_namespace n ON n.oid = c.relnamespace " +
            $"WHERE {NonSystemSchemas.Replace("nspname", "n.nspname")} AND a.attnum > 0 AND NOT a.attisdropped AND a.attname ILIKE '%club\\_id%'"))
            .ShouldBeEmpty();
        (await QueryAsync(cs,
            "SELECT n.nspname || '.' || p.proname FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace " +
            $"WHERE {NonSystemSchemas.Replace("nspname", "n.nspname")} AND (p.proname ILIKE '%club\\_id%' " +
            "OR EXISTS (SELECT 1 FROM unnest(coalesce(p.proargnames, ARRAY[]::text[])) AS arg(name) WHERE arg.name ILIKE '%club\\_id%'))"))
            .ShouldBeEmpty();
        (await QueryAsync(cs,
            "SELECT n.nspname || '.' || t.typname FROM pg_type t JOIN pg_namespace n ON n.oid = t.typnamespace " +
            $"WHERE {NonSystemSchemas.Replace("nspname", "n.nspname")} AND t.typname ILIKE '%club\\_id%'"))
            .ShouldBeEmpty();
    }
}
