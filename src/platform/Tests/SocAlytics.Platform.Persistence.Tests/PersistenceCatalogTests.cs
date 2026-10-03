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

public sealed class PersistenceCatalogTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine").Build();
    private string _connectionString = null!;

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();
        var name = "db_" + Guid.NewGuid().ToString("N");
        await using (var admin = new NpgsqlConnection(_container.GetConnectionString()))
        {
            await admin.OpenAsync(TestContext.Current.CancellationToken);
            await using var create = new NpgsqlCommand($"CREATE DATABASE {name}", admin);
            await create.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        _connectionString = new NpgsqlConnectionStringBuilder(_container.GetConnectionString()) { Database = name }.ConnectionString;
        var services = new ServiceCollection();
        services.AddPlatformPersistence(o => o.ConnectionString = _connectionString);
        services.AddClubModule();
        services.AddIdentityAccessModule();
        services.AddRecordingsModule();
        services.AddRegistryModule();
        services.AddAnalysisModule();
        services.AddAgentOrchestrationModule();
        await using var provider = services.BuildServiceProvider();
        await provider.GetRequiredService<MigrationRunner>().RunAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    private async Task<List<string>> QueryAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var rows = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            rows.Add(reader.GetString(0));
        }

        return rows;
    }

    [Fact]
    public async Task ExactlyTheAdoptedSchemasExistWithTheirOwnerRoles()
    {
        var rows = await QueryAsync(
            """
            SELECT nspname || '=' || pg_get_userbyid(nspowner) FROM pg_namespace
            WHERE nspname NOT LIKE 'pg\_%' AND nspname NOT IN ('information_schema', 'public') ORDER BY 1
            """);

        var expected = ModuleKey.All.Select(m => $"{m.Schema}={m.OwnerRole}")
            .Append("socalytics_migrations=" + await CurrentOwnerAsync("socalytics_migrations"))
            .Order(StringComparer.Ordinal).ToList();
        rows.ShouldBe(expected);
        rows.Count.ShouldBe(7);
    }

    private async Task<string> CurrentOwnerAsync(string schema) =>
        (await QueryAsync("SELECT pg_get_userbyid(nspowner) FROM pg_namespace WHERE nspname = @n", ("n", schema))).Single();

    [Fact]
    public async Task MigrationHistoryHasExactlyTheAdoptedFields()
    {
        var columns = await QueryAsync(
            """
            SELECT column_name || ':' || data_type || ':' || is_nullable FROM information_schema.columns
            WHERE table_schema = 'socalytics_migrations' AND table_name = 'history' ORDER BY ordinal_position
            """);
        columns.ShouldBe([
            "module:text:NO",
            "sequence:integer:NO",
            "script_name:text:NO",
            "checksum:text:NO",
            "applied_at:timestamp with time zone:NO",
        ]);

        var constraints = await QueryAsync(
            "SELECT conname::text || ':' || contype::text FROM pg_constraint WHERE conrelid = 'socalytics_migrations.history'::regclass ORDER BY 1");
        constraints.ShouldBe(["pk_history:p", "uq_history_module_sequence:u"]);

        var rows = await QueryAsync("SELECT module || '/' || sequence || '/' || length(checksum) FROM socalytics_migrations.history ORDER BY 1");
        rows.ShouldBe(ModuleKey.All.Select(m => m.Name + "/1/64").Order(StringComparer.Ordinal).ToList());
    }

    [Fact]
    public async Task RuntimeRolesCannotCreateInOrReachAnySchemaTheyDoNotUse()
    {
        foreach (var module in ModuleKey.All)
        {
            foreach (var schema in ModuleKey.All.Select(m => m.Schema).Append("socalytics_migrations"))
            {
                var own = schema == module.Schema;
                var privileges = await QueryAsync(
                    "SELECT has_schema_privilege(@role, @schema, 'USAGE')::text || ',' || has_schema_privilege(@role, @schema, 'CREATE')::text",
                    ("role", module.RuntimeRole), ("schema", schema));
                privileges.Single().ShouldBe(own ? "true,false" : "false,false", $"{module.RuntimeRole} on {schema}");
            }

            var history = await QueryAsync(
                "SELECT has_table_privilege(@role, 'socalytics_migrations.history', 'SELECT,INSERT,UPDATE,DELETE')::text",
                ("role", module.RuntimeRole));
            history.Single().ShouldBe("false");

            var superuser = await QueryAsync(
                "SELECT (rolsuper OR rolcreaterole OR rolcreatedb OR rolbypassrls OR rolcanlogin)::text FROM pg_roles WHERE rolname = @role",
                ("role", module.RuntimeRole));
            superuser.Single().ShouldBe("false");
        }
    }

    [Fact]
    public async Task NoClubIdAppearsInAnySchemaTableViewColumnOrFunctionArgument()
    {
        const string productSchemas = "nspname NOT LIKE 'pg\\_%' AND nspname <> 'information_schema'";

        (await QueryAsync($"SELECT nspname FROM pg_namespace WHERE {productSchemas} AND nspname ILIKE '%club\\_id%'")).ShouldBeEmpty();
        (await QueryAsync(
            $"""
            SELECT n.nspname || '.' || c.relname FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE {productSchemas} AND c.relname ILIKE '%club\_id%'
            """)).ShouldBeEmpty();
        (await QueryAsync(
            $"""
            SELECT n.nspname || '.' || c.relname || '.' || a.attname FROM pg_attribute a
            JOIN pg_class c ON c.oid = a.attrelid JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE {productSchemas} AND a.attnum > 0 AND NOT a.attisdropped AND a.attname ILIKE '%club\_id%'
            """)).ShouldBeEmpty();
        (await QueryAsync(
            $"""
            SELECT n.nspname || '.' || p.proname FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
            WHERE {productSchemas}
              AND (p.proname ILIKE '%club\_id%' OR EXISTS (
                  SELECT 1 FROM unnest(coalesce(p.proargnames, ARRAY[]::text[])) AS arg(name) WHERE arg.name ILIKE '%club\_id%'))
            """)).ShouldBeEmpty();
    }
}
