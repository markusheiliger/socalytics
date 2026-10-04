using System.Text;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using Testcontainers.PostgreSql;
using Xunit;

namespace SocAlytics.Platform.Persistence.Tests;

public sealed class RoleIsolationTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine").Build();
    private ServiceProvider _provider = null!;
    private string _connectionString = null!;

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync(TestContext.Current.CancellationToken);
        _connectionString = new NpgsqlConnectionStringBuilder(_container.GetConnectionString()) { Pooling = false }
            .ConnectionString;

        var services = new ServiceCollection().AddPlatformPersistence(_container.GetConnectionString());
        foreach (var module in new[] { PersistenceModuleKey.Club, PersistenceModuleKey.Registry })
        {
            var sql = $"""
                CREATE SCHEMA {module.Schema};
                GRANT USAGE ON SCHEMA {module.Schema} TO {module.RuntimeRole};
                CREATE TABLE {module.Schema}.items (id int PRIMARY KEY, value text);
                """;
            services.AddModulePersistence(new Contributor(module,
                new MigrationDescriptor(module, 1, "0001", Encoding.UTF8.GetBytes(sql))));
        }

        _provider = services.BuildServiceProvider();
        await _provider.GetRequiredService<MigrationOrchestrator>().MigrateAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _provider.DisposeAsync();
        await _container.DisposeAsync();
    }

    private sealed class Contributor(PersistenceModuleKey module, params MigrationDescriptor[] migrations)
        : IMigrationContributor
    {
        public PersistenceModuleKey Module { get; } = module;

        public IReadOnlyList<MigrationDescriptor> GetMigrations() => migrations;
    }

    private Task<NpgsqlConnection> Session(PersistenceModuleKey module) =>
        _provider.GetRequiredKeyedService<IModuleConnectionFactory>(module.Key)
            .OpenConnectionAsync(TestContext.Current.CancellationToken);

    private async Task<T> AdminAsync<T>(string sql)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        return await connection.ExecuteScalarAsync<T>(sql) ?? throw new InvalidOperationException();
    }

    [Fact]
    public async Task EachSchemaIsOwnedByItsOwnerRoleOnly()
    {
        (await AdminAsync<string>("SELECT string_agg(nspname || ':' || pg_get_userbyid(nspowner), ',' ORDER BY nspname) FROM pg_namespace WHERE nspname IN ('club','registry')"))
            .ShouldBe("club:club_owner,registry:registry_owner");
        (await AdminAsync<string>("SELECT pg_get_userbyid(relowner) FROM pg_class WHERE oid = 'registry.items'::regclass"))
            .ShouldBe("registry_owner");
        (await AdminAsync<long>("SELECT count(*) FROM pg_roles WHERE rolname LIKE '%\\_owner' OR rolname LIKE '%\\_runtime'"))
            .ShouldBe(12);
        (await AdminAsync<long>("SELECT count(*) FROM pg_roles WHERE (rolname LIKE '%\\_owner' OR rolname LIKE '%\\_runtime') AND (rolcanlogin OR rolsuper OR rolcreaterole OR rolcreatedb)"))
            .ShouldBe(0);
    }

    [Fact]
    public async Task RuntimeSessionAccessesOnlyOwnSchema()
    {
        await using var club = await Session(PersistenceModuleKey.Club);
        (await club.ExecuteScalarAsync<string>("SELECT current_user")).ShouldBe("club_runtime");
        await club.ExecuteAsync("INSERT INTO club.items VALUES (1, 'a')");
        (await club.ExecuteScalarAsync<long>("SELECT count(*) FROM club.items")).ShouldBe(1);
    }

    [Fact]
    public async Task CrossSchemaReadsAndWritesFailWithoutCommittingAnything()
    {
        await using (var registry = await Session(PersistenceModuleKey.Registry))
        {
            await registry.ExecuteAsync("INSERT INTO registry.items VALUES (1, 'kept')");
        }

        await using (var club = await Session(PersistenceModuleKey.Club))
        {
            await AssertDeniedAsync(club, "SELECT * FROM registry.items");
            await AssertDeniedAsync(club, "INSERT INTO registry.items VALUES (2, 'x')");
            await AssertDeniedAsync(club, "UPDATE registry.items SET value = 'x'");
            await AssertDeniedAsync(club, "DELETE FROM registry.items");
            await AssertDeniedAsync(club, "CREATE TABLE registry.rogue (id int)");
            await AssertDeniedAsync(club, "SELECT * FROM socalytics_migrations.history");
            await AssertDeniedAsync(club, "CREATE TABLE club.rogue (id int)");

            await using var transaction = await club.BeginTransactionAsync(TestContext.Current.CancellationToken);
            await club.ExecuteAsync("INSERT INTO club.items VALUES (10, 'rolled back')", transaction: transaction);
            await AssertDeniedAsync(club, "INSERT INTO registry.items VALUES (3, 'x')", transaction);
            await transaction.RollbackAsync(TestContext.Current.CancellationToken);
        }

        (await AdminAsync<string>("SELECT string_agg(id::text || value, ',') FROM registry.items")).ShouldBe("1kept");
        (await AdminAsync<long>("SELECT count(*) FROM club.items")).ShouldBe(0);
        (await AdminAsync<long>("SELECT count(*) FROM pg_class WHERE relname = 'rogue'")).ShouldBe(0);
    }

    [Fact]
    public async Task PooledSessionsDoNotLeakRoleAcrossModules()
    {
        await using (var club = await Session(PersistenceModuleKey.Club))
        {
            (await club.ExecuteScalarAsync<string>("SELECT current_user")).ShouldBe("club_runtime");
        }

        await using var registry = await Session(PersistenceModuleKey.Registry);
        (await registry.ExecuteScalarAsync<string>("SELECT current_user")).ShouldBe("registry_runtime");
    }

    private static async Task AssertDeniedAsync(NpgsqlConnection connection, string sql, NpgsqlTransaction? transaction = null)
    {
        if (transaction is null)
        {
            var ex = await Should.ThrowAsync<PostgresException>(() => connection.ExecuteAsync(sql));
            ex.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        }
        else
        {
            // A savepoint keeps the surrounding transaction usable after the denied statement.
            await transaction.SaveAsync("denied");
            var ex = await Should.ThrowAsync<PostgresException>(() => connection.ExecuteAsync(sql, transaction: transaction));
            ex.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
            await transaction.RollbackAsync("denied");
        }
    }
}
