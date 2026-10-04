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

public sealed class CatalogTests : IAsyncLifetime
{
    private static readonly string[] ModuleSchemas = [.. PersistenceModuleKey.All.Select(m => m.Schema)];

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine").Build();
    private ServiceProvider _provider = null!;
    private string _connectionString = null!;

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync(TestContext.Current.CancellationToken);
        _connectionString = new NpgsqlConnectionStringBuilder(_container.GetConnectionString()) { Pooling = false }
            .ConnectionString;

        var services = new ServiceCollection().AddPlatformPersistence(_container.GetConnectionString());
        services.AddClubModule()
            .AddIdentityAccessModule()
            .AddRecordingsModule()
            .AddRegistryModule()
            .AddAnalysisModule()
            .AddAgentOrchestrationModule();
        _provider = services.BuildServiceProvider();
        await _provider.GetRequiredService<MigrationOrchestrator>().MigrateAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _provider.DisposeAsync();
        await _container.DisposeAsync();
    }

    private async Task<IReadOnlyList<T>> QueryAsync<T>(string sql, object? parameters = null)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        return (await connection.QueryAsync<T>(sql, parameters)).ToList();
    }

    [Fact]
    public async Task DisposableContainerIsUsedInsteadOfAnExternalDatabase()
    {
        _container.GetConnectionString().ShouldContain(_container.Hostname);
        (await QueryAsync<string>("SELECT current_database()")).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task SchemasAreExactlyTheSixModulesAndMigrationsOwnedByTheirRoles()
    {
        var owned = await QueryAsync<string>(
            "SELECT nspname || ':' || pg_get_userbyid(nspowner) FROM pg_namespace " +
            "WHERE nspname = ANY(@modules) OR nspname = 'socalytics_migrations' ORDER BY nspname COLLATE \"C\"",
            new { modules = ModuleSchemas });

        owned.ShouldBe(
            [.. PersistenceModuleKey.All
                .Select(m => $"{m.Schema}:{m.OwnerRole}")
                .Append("socalytics_migrations:" + await Superuser())
                .Order(StringComparer.Ordinal)]);

        var userSchemas = await QueryAsync<string>(
            "SELECT nspname FROM pg_namespace WHERE nspname NOT LIKE 'pg\\_%' AND nspname <> 'information_schema' AND nspname <> 'public' ORDER BY nspname COLLATE \"C\"");
        userSchemas.ShouldBe([.. ModuleSchemas.Append("socalytics_migrations").Order(StringComparer.Ordinal)]);
    }

    private async Task<string> Superuser() =>
        (await QueryAsync<string>("SELECT current_user")).Single();

    [Fact]
    public async Task MigrationHistoryHasExactlyTheDocumentedColumns()
    {
        var columns = await QueryAsync<string>(
            "SELECT column_name || ':' || data_type || ':' || is_nullable FROM information_schema.columns " +
            "WHERE table_schema = 'socalytics_migrations' AND table_name = 'history' ORDER BY ordinal_position");

        columns.ShouldBe(
        [
            "module:text:NO",
            "sequence:integer:NO",
            "identity:text:NO",
            "checksum:text:NO",
            "applied_at:timestamp with time zone:NO",
        ]);

        (await QueryAsync<string>(
            "SELECT c.relname FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace " +
            "WHERE n.nspname = 'socalytics_migrations' AND c.relkind IN ('r','p','v','m','f') ORDER BY 1"))
            .ShouldBe(["history"]);
    }

    [Fact]
    public async Task RuntimeRolesHaveNoPrivilegesBeyondTheirOwnSchemaUsage()
    {
        foreach (var module in PersistenceModuleKey.All)
        {
            foreach (var schema in ModuleSchemas.Append("socalytics_migrations"))
            {
                var role = module.RuntimeRole;
                var own = schema == module.Schema;
                (await QueryAsync<bool>("SELECT has_schema_privilege(@role, @schema, 'USAGE')", new { role, schema }))
                    .Single().ShouldBe(own, $"{role} USAGE on {schema}");
                (await QueryAsync<bool>("SELECT has_schema_privilege(@role, @schema, 'CREATE')", new { role, schema }))
                    .Single().ShouldBeFalse($"{role} CREATE on {schema}");
            }

            (await QueryAsync<bool>("SELECT has_table_privilege(@role, 'socalytics_migrations.history', 'SELECT,INSERT,UPDATE,DELETE')",
                new { role = module.RuntimeRole })).Single().ShouldBeFalse();
            (await QueryAsync<bool>("SELECT rolcanlogin OR rolsuper OR rolcreaterole OR rolcreatedb FROM pg_roles WHERE rolname = @role",
                new { role = module.RuntimeRole })).Single().ShouldBeFalse();
        }
    }

    [Fact]
    public async Task RuntimeSessionIsDeniedAccessToPeerSchemasAndMigrationHistory()
    {
        foreach (var module in PersistenceModuleKey.All)
        {
            await using var session = await _provider.GetRequiredKeyedService<IModuleConnectionFactory>(module.Key)
                .OpenConnectionAsync(TestContext.Current.CancellationToken);
            (await session.ExecuteScalarAsync<string>("SELECT current_user")).ShouldBe(module.RuntimeRole);

            var targets = PersistenceModuleKey.All.Where(m => m != module).Select(m => $"CREATE TABLE {m.Schema}.rogue (id int)")
                .Append("CREATE TABLE socalytics_migrations.rogue (id int)")
                .Append("SELECT * FROM socalytics_migrations.history")
                .Append($"CREATE TABLE {module.Schema}.rogue (id int)");
            foreach (var sql in targets)
            {
                var ex = await Should.ThrowAsync<PostgresException>(() => session.ExecuteAsync(sql));
                ex.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege, sql);
            }
        }

        (await QueryAsync<string>("SELECT relname FROM pg_class WHERE relname = 'rogue'")).ShouldBeEmpty();
    }

    [Fact]
    public async Task CatalogContainsNoClubIdAnywhere()
    {
        (await QueryAsync<string>(
            "SELECT nspname FROM pg_namespace WHERE nspname ILIKE '%club\\_id%'")).ShouldBeEmpty();

        (await QueryAsync<string>(
            "SELECT n.nspname || '.' || c.relname FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace " +
            "WHERE n.nspname NOT LIKE 'pg\\_%' AND n.nspname <> 'information_schema' AND c.relname ILIKE '%club\\_id%'"))
            .ShouldBeEmpty();

        (await QueryAsync<string>(
            "SELECT table_schema || '.' || table_name || '.' || column_name FROM information_schema.columns " +
            "WHERE table_schema NOT LIKE 'pg\\_%' AND table_schema <> 'information_schema' AND column_name ILIKE '%club\\_id%'"))
            .ShouldBeEmpty();

        (await QueryAsync<string>(
            "SELECT n.nspname || '.' || p.proname FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace " +
            "WHERE n.nspname = ANY(@schemas) AND (p.proname ILIKE '%club\\_id%' OR " +
            "COALESCE(array_to_string(p.proargnames, ','), '') ILIKE '%club\\_id%')",
            new { schemas = ModuleSchemas.Append("socalytics_migrations").ToArray() }))
            .ShouldBeEmpty();

        (await QueryAsync<string>(
            "SELECT schemaname || '.' || viewname FROM pg_views WHERE schemaname = ANY(@schemas) AND definition ILIKE '%club\\_id%'",
            new { schemas = ModuleSchemas.Append("socalytics_migrations").ToArray() }))
            .ShouldBeEmpty();
    }
}
