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
        var applicationSchemas = (await connection.QueryAsync<string>("""
            SELECT nspname
            FROM pg_namespace
            WHERE nspname NOT LIKE 'pg_%'
              AND nspname NOT IN ('information_schema', 'public')
            ORDER BY nspname
            """)).ToArray();
        applicationSchemas.ShouldBe(schemas.Append("socalytics_migrations").Order().ToArray());

        var schemaOwners = (await connection.QueryAsync<SchemaOwner>("""
            SELECT nspname AS SchemaName, pg_get_userbyid(nspowner) AS OwnerName
            FROM pg_namespace
            WHERE nspname = ANY(@schemas)
            """, new { schemas = applicationSchemas }))
            .ToDictionary(schema => schema.SchemaName, schema => schema.OwnerName);
        foreach (var module in Enum.GetValues<ModuleKey>())
        {
            schemaOwners[module.ToSchemaName()].ShouldBe($"socalytics_{module.ToSchemaName()}_owner");
        }

        schemaOwners["socalytics_migrations"].ShouldBe(
            await connection.ExecuteScalarAsync<string>("SELECT current_user"));

        var historyColumns = (await connection.QueryAsync<HistoryColumn>("""
            SELECT column_name AS Name, data_type AS DataType, is_nullable AS IsNullable, column_default AS ColumnDefault
            FROM information_schema.columns
            WHERE table_schema = 'socalytics_migrations' AND table_name = 'history'
            ORDER BY ordinal_position
            """)).ToArray();
        historyColumns.Select(column => column.Name).ShouldBe(
            ["module_key", "sequence", "script_identity", "checksum", "applied_at"]);
        var columnsByName = historyColumns.ToDictionary(column => column.Name);
        foreach (var name in new[] { "module_key", "script_identity", "checksum" })
        {
            columnsByName[name].DataType.ShouldBe("text");
            columnsByName[name].IsNullable.ShouldBe("NO");
            columnsByName[name].ColumnDefault.ShouldBeNull();
        }

        columnsByName["sequence"].DataType.ShouldBe("integer");
        columnsByName["sequence"].IsNullable.ShouldBe("NO");
        columnsByName["sequence"].ColumnDefault.ShouldBeNull();
        columnsByName["applied_at"].DataType.ShouldBe("timestamp with time zone");
        columnsByName["applied_at"].IsNullable.ShouldBe("NO");
        columnsByName["applied_at"].ColumnDefault!.ToLowerInvariant().ShouldContain("now()");

        var historyRows = (await connection.QueryAsync<HistoryRow>("""
            SELECT module_key AS ModuleKey, sequence AS Sequence, script_identity AS ScriptIdentity,
                   checksum AS Checksum, applied_at AS AppliedAt
            FROM socalytics_migrations.history
            """)).ToArray();
        historyRows.Length.ShouldBe(6);
        historyRows.Select(row => row.ModuleKey).Order()
            .ShouldBe(Enum.GetValues<ModuleKey>().Select(module => module.ToString()).Order());
        foreach (var row in historyRows)
        {
            row.Sequence.ShouldBe(1);
            row.ScriptIdentity.ShouldBe("0001_initial");
            row.Checksum.Length.ShouldBe(64);
            row.AppliedAt.ShouldBeGreaterThan(DateTime.MinValue);
        }

        foreach (var module in Enum.GetValues<ModuleKey>())
        {
            var runtimeRole = $"socalytics_{module.ToSchemaName()}_runtime";
            (await HasSchemaPrivilege(connection, runtimeRole, module.ToSchemaName(), "USAGE")).ShouldBeTrue();
            (await HasSchemaPrivilege(connection, runtimeRole, module.ToSchemaName(), "CREATE")).ShouldBeFalse();
            (await HasSchemaPrivilege(connection, runtimeRole, "socalytics_migrations", "USAGE")).ShouldBeFalse();
            (await connection.ExecuteScalarAsync<bool>(
                "SELECT has_table_privilege(@role, 'socalytics_migrations.history', 'SELECT, INSERT, UPDATE, DELETE')",
                new { role = runtimeRole })).ShouldBeFalse();

            foreach (var peer in Enum.GetValues<ModuleKey>().Where(peer => peer != module))
            {
                (await HasSchemaPrivilege(connection, runtimeRole, peer.ToSchemaName(), "USAGE")).ShouldBeFalse();
                (await HasSchemaPrivilege(connection, runtimeRole, peer.ToSchemaName(), "CREATE")).ShouldBeFalse();
            }
        }

        (await connection.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'socalytics_migrations' AND table_name = 'history'"))
            .ShouldBe(1);
        (await connection.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM socalytics_migrations.history")).ShouldBe(6);
        (await connection.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM information_schema.tables WHERE table_schema = ANY(@schemas) AND table_type = 'BASE TABLE'",
            new { schemas })).ShouldBe(0);

        (await connection.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM pg_namespace WHERE lower(nspname) = 'club_id'")).ShouldBe(0);
        (await connection.ExecuteScalarAsync<long>("""
            SELECT count(*)
            FROM pg_class AS r
            JOIN pg_namespace AS n ON n.oid = r.relnamespace
            WHERE n.nspname NOT LIKE 'pg_%'
              AND n.nspname <> 'information_schema'
              AND r.relkind IN ('r', 'p', 'v', 'm', 'f')
              AND lower(r.relname) = 'club_id'
            """)).ShouldBe(0);
        (await connection.ExecuteScalarAsync<long>("""
            SELECT count(*)
            FROM pg_attribute AS a
            JOIN pg_class AS r ON r.oid = a.attrelid
            JOIN pg_namespace AS n ON n.oid = r.relnamespace
            WHERE n.nspname NOT LIKE 'pg_%'
              AND n.nspname <> 'information_schema'
              AND a.attnum > 0
              AND NOT a.attisdropped
              AND lower(a.attname) = 'club_id'
            """)).ShouldBe(0);
        (await connection.ExecuteScalarAsync<long>("""
            SELECT count(*)
            FROM pg_proc AS p
            JOIN pg_namespace AS n ON n.oid = p.pronamespace
            CROSS JOIN LATERAL unnest(COALESCE(p.proargnames, ARRAY[]::text[])) AS arguments(name)
            WHERE n.nspname NOT LIKE 'pg_%'
              AND n.nspname <> 'information_schema'
              AND lower(arguments.name) = 'club_id'
            """)).ShouldBe(0);
    }

    private static Task<bool> HasSchemaPrivilege(
        NpgsqlConnection connection,
        string role,
        string schema,
        string privilege) =>
        connection.ExecuteScalarAsync<bool>(
            "SELECT has_schema_privilege(@role, @schema, @privilege)",
            new { role, schema, privilege });

    private sealed class SchemaOwner
    {
        public string SchemaName { get; set; } = string.Empty;
        public string OwnerName { get; set; } = string.Empty;
    }

    private sealed class HistoryColumn
    {
        public string Name { get; set; } = string.Empty;
        public string DataType { get; set; } = string.Empty;
        public string IsNullable { get; set; } = string.Empty;
        public string? ColumnDefault { get; set; }
    }

    private sealed class HistoryRow
    {
        public string ModuleKey { get; set; } = string.Empty;
        public int Sequence { get; set; }
        public string ScriptIdentity { get; set; } = string.Empty;
        public string Checksum { get; set; } = string.Empty;
        public DateTime AppliedAt { get; set; }
    }
}
