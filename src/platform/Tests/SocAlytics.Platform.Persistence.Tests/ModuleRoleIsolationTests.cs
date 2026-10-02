using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using SocAlytics.Platform.Persistence;
using Testcontainers.PostgreSql;
using Xunit;

namespace SocAlytics.Platform.Persistence.Tests;

public sealed class ModuleRoleIsolationTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _database = new PostgreSqlBuilder("postgres:17-alpine").Build();
    private ServiceProvider _services = null!;

    public async ValueTask InitializeAsync()
    {
        await _database.StartAsync(TestContext.Current.CancellationToken);
        var services = new ServiceCollection();
        services.AddPlatformPersistence(_database.GetConnectionString(), _database.GetConnectionString());
        foreach (var module in PersistenceModuleIdentity.All)
        {
            services.AddSingleton(Migration(module, "initial", module == PersistenceModuleIdentity.Club
                ? "roles_objects"
                : module == PersistenceModuleIdentity.Registry ? "roles_objects_registry" : null));
        }

        _services = services.BuildServiceProvider();
        await _services.MigratePlatformDatabaseAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        await _database.DisposeAsync();
    }

    [Fact]
    public async Task Each_module_has_distinct_nologin_owner_and_runtime_roles_and_owns_only_its_schema()
    {
        var roles = await QueryAsync(
            """
            SELECT rolname, rolcanlogin, rolsuper, rolcreaterole FROM pg_roles
            WHERE rolname ~ '^(club|identity_access|recordings|registry|analysis|agent_orchestration)_(owner|runtime)$' ORDER BY rolname
            """,
            reader => (reader.GetString(0), reader.GetBoolean(1), reader.GetBoolean(2), reader.GetBoolean(3)));
        roles.Count.ShouldBe(12);
        roles.ShouldAllBe(role => !role.Item2 && !role.Item3 && !role.Item4);

        var owners = await QueryAsync(
            """
            SELECT nspname, pg_get_userbyid(nspowner) FROM pg_namespace
            WHERE nspname IN ('club', 'registry') ORDER BY nspname
            """,
            reader => (reader.GetString(0), reader.GetString(1)));
        owners.ShouldBe([("club", "club_owner"), ("registry", "registry_owner")]);

        var tables = await QueryAsync(
            "SELECT schemaname, tableowner FROM pg_tables WHERE tablename = 'items' ORDER BY schemaname",
            reader => (reader.GetString(0), reader.GetString(1)));
        tables.ShouldBe([("club", "club_owner"), ("registry", "registry_owner")]);
    }

    [Fact]
    public async Task Module_session_accesses_its_own_schema_as_the_runtime_role()
    {
        await using var connection = await Factory(PersistenceModuleIdentity.Club).OpenConnectionAsync(
            TestContext.Current.CancellationToken);

        (await ScalarAsync(connection, "SELECT current_user")).ShouldBe("club_runtime");
        (await ScalarAsync(connection, "SELECT value FROM club.items WHERE id = 1")).ShouldBe("club");
        await ExecuteAsync(connection, "INSERT INTO club.items VALUES (2, 'written')");
        await ExecuteAsync(connection, "UPDATE club.items SET value = 'changed' WHERE id = 2");
        await ExecuteAsync(connection, "DELETE FROM club.items WHERE id = 2");
    }

    [Theory]
    [InlineData("SELECT value FROM registry.items")]
    [InlineData("INSERT INTO registry.items VALUES (2, 'intruder')")]
    [InlineData("UPDATE registry.items SET value = 'intruder'")]
    [InlineData("DELETE FROM registry.items")]
    [InlineData("SELECT registry.secret()")]
    [InlineData("CREATE TABLE registry.intruder (id integer)")]
    [InlineData("CREATE TABLE club.runtime_ddl (id integer)")]
    [InlineData("CREATE TABLE public.runtime_ddl (id integer)")]
    [InlineData("SELECT * FROM socalytics_migrations.history")]
    [InlineData("INSERT INTO socalytics_migrations.history VALUES ('club', 99, 'x', repeat('a', 64), now())")]
    public async Task Peer_and_migration_schema_access_is_denied_and_commits_nothing(string statement)
    {
        await using (var connection = await Factory(PersistenceModuleIdentity.Club).OpenConnectionAsync(
            TestContext.Current.CancellationToken))
        {
            await using var transaction = await connection.BeginTransactionAsync(TestContext.Current.CancellationToken);
            await ExecuteAsync(connection, "INSERT INTO club.items VALUES (3, 'rolled-back')", transaction);

            var error = await Should.ThrowAsync<PostgresException>(() => ExecuteAsync(connection, statement, transaction));
            error.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        }

        (await QueryAsync("SELECT count(*) FROM club.items", reader => reader.GetInt64(0))).ShouldBe([1L]);
        (await QueryAsync("SELECT count(*) FROM registry.items", reader => reader.GetInt64(0))).ShouldBe([1L]);
        (await QueryAsync("SELECT count(*) FROM socalytics_migrations.history", reader => reader.GetInt64(0)))
            .ShouldBe([6L]);
        (await QueryAsync(
            "SELECT count(*) FROM pg_class WHERE relname IN ('intruder', 'runtime_ddl')",
            reader => reader.GetInt64(0))).ShouldBe([0L]);
    }

    [Fact]
    public async Task Peer_runtime_cannot_use_owner_role_or_escape_to_another_runtime_role_object_access()
    {
        await using var connection = await Factory(PersistenceModuleIdentity.Registry).OpenConnectionAsync(
            TestContext.Current.CancellationToken);

        (await ScalarAsync(connection, "SELECT value FROM registry.items WHERE id = 1")).ShouldBe("registry");
        await Should.ThrowAsync<PostgresException>(() => ExecuteAsync(connection, "SELECT value FROM club.items"));
    }

    [Fact]
    public async Task Pooled_module_connections_always_start_as_their_runtime_role()
    {
        var club = Factory(PersistenceModuleIdentity.Club);
        var registry = Factory(PersistenceModuleIdentity.Registry);

        for (var i = 0; i < 3; i++)
        {
            await using (var connection = await club.OpenConnectionAsync(TestContext.Current.CancellationToken))
            {
                (await ScalarAsync(connection, "SELECT current_user")).ShouldBe("club_runtime");
            }

            await using (var connection = await registry.OpenConnectionAsync(TestContext.Current.CancellationToken))
            {
                (await ScalarAsync(connection, "SELECT current_user")).ShouldBe("registry_runtime");
            }
        }
    }

    [Fact]
    public async Task Repeated_migration_keeps_roles_and_privileges_stable()
    {
        await _services.MigratePlatformDatabaseAsync(TestContext.Current.CancellationToken);

        await using var connection = await Factory(PersistenceModuleIdentity.Club).OpenConnectionAsync(
            TestContext.Current.CancellationToken);
        (await ScalarAsync(connection, "SELECT value FROM club.items WHERE id = 1")).ShouldBe("club");
        await Should.ThrowAsync<PostgresException>(() => ExecuteAsync(connection, "SELECT value FROM registry.items"));
    }

    [Fact]
    public void Module_factory_rejects_unadopted_identity_and_exposes_no_bootstrap_connection()
    {
        var runtime = _services.GetRequiredService<IRuntimeDatabaseConnectionFactory>();

        Should.Throw<ArgumentNullException>(() => runtime.ForModule(null!));
        typeof(IModuleDatabaseConnectionFactory).GetProperties().Select(property => property.Name)
            .ShouldBe(["Module"]);
    }

    private IModuleDatabaseConnectionFactory Factory(PersistenceModuleIdentity module) =>
        _services.GetRequiredService<IRuntimeDatabaseConnectionFactory>().ForModule(module);

    private static MigrationDescriptor Migration(PersistenceModuleIdentity module, string identity, string? fixture)
    {
        return MigrationDescriptor.FromEmbeddedResource(
            module,
            1,
            identity,
            typeof(ModuleRoleIsolationTests).Assembly,
            fixture is null
                ? "SocAlytics.Platform.Persistence.Tests.sample.sql"
                : $"SocAlytics.Platform.Persistence.Tests.Fixtures.Migrations.{fixture}.sql");
    }

    private static async Task ExecuteAsync(
        NpgsqlConnection connection, string sql, NpgsqlTransaction? transaction = null)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<object?> ScalarAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
    }

    private async Task<List<T>> QueryAsync<T>(string sql, Func<NpgsqlDataReader, T> map)
    {
        await using var connection = new NpgsqlConnection(_database.GetConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        var rows = new List<T>();
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            rows.Add(map(reader));
        }

        return rows;
    }
}
