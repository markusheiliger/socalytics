using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using Xunit;

namespace SocAlytics.Platform.Persistence.Tests;

public sealed class RoleIsolationTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static MigrationDescriptor Schema(PersistenceModuleKey m) => new(m, 1, "schema", Encoding.UTF8.GetBytes($"""
        CREATE SCHEMA {m.SchemaName} AUTHORIZATION {m.OwnerRoleName};
        REVOKE ALL ON SCHEMA {m.SchemaName} FROM PUBLIC;
        GRANT USAGE ON SCHEMA {m.SchemaName} TO {m.RuntimeRoleName};
        SET LOCAL ROLE {m.OwnerRoleName};
        CREATE TABLE {m.SchemaName}.item (id int PRIMARY KEY, name text);
        GRANT SELECT, INSERT, UPDATE, DELETE ON {m.SchemaName}.item TO {m.RuntimeRoleName};
        """));

    private async Task<ServiceProvider> SetUpAsync()
    {
        var cs = await postgres.CreateDatabaseAsync();
        var services = new ServiceCollection();
        services.AddPlatformPersistence(o => o.BootstrapConnectionString = cs);
        foreach (var module in PersistenceModuleKey.All)
        {
            services.AddModuleMigrations(new Contributor(module));
            services.AddModulePersistence(module);
        }

        var provider = services.BuildServiceProvider();
        await provider.GetRequiredService<IMigrationRunner>().MigrateAsync();
        return provider;
    }

    private static async Task<NpgsqlConnection> OpenAsync(ServiceProvider p, PersistenceModuleKey m) =>
        await p.GetRequiredKeyedService<IModuleConnectionFactory>(m).OpenAsync();

    private static async Task<object?> ScalarAsync(NpgsqlConnection c, string sql, NpgsqlTransaction? tx = null)
    {
        await using var command = new NpgsqlCommand(sql, c, tx);
        return await command.ExecuteScalarAsync();
    }

    [Fact]
    public async Task RolesAreNoLoginAndEachSchemaIsOwnedByItsOwnerRole()
    {
        await using var provider = await SetUpAsync();
        await using var c = await OpenAsync(provider, PersistenceModuleKey.Club);

        foreach (var m in PersistenceModuleKey.All)
        {
            (await ScalarAsync(c, $"SELECT count(*) FROM pg_roles WHERE rolname IN ('{m.OwnerRoleName}','{m.RuntimeRoleName}') AND NOT rolcanlogin AND NOT rolsuper"))
                .ShouldBe(2L);
            (await ScalarAsync(c, $"SELECT nspowner::regrole::text FROM pg_namespace WHERE nspname = '{m.SchemaName}'"))
                .ShouldBe(m.OwnerRoleName);
            (await ScalarAsync(c, $"SELECT tableowner FROM pg_tables WHERE schemaname = '{m.SchemaName}' AND tablename = 'item'"))
                .ShouldBe(m.OwnerRoleName);
        }
    }

    [Fact]
    public async Task SessionRunsAsModuleRuntimeRoleAndAccessesOwnSchema()
    {
        await using var provider = await SetUpAsync();

        foreach (var m in PersistenceModuleKey.All)
        {
            await using var c = await OpenAsync(provider, m);
            (await ScalarAsync(c, "SELECT current_user")).ShouldBe(m.RuntimeRoleName);
            await ScalarAsync(c, $"INSERT INTO {m.SchemaName}.item VALUES (1, 'x')");
            (await ScalarAsync(c, $"SELECT name FROM {m.SchemaName}.item WHERE id = 1")).ShouldBe("x");
        }
    }

    [Fact]
    public async Task CrossSchemaReadsAndWritesAreDenied()
    {
        await using var provider = await SetUpAsync();

        foreach (var m in PersistenceModuleKey.All)
        {
            foreach (var peer in PersistenceModuleKey.All.Where(k => k != m))
            {
                await using var c = await OpenAsync(provider, m);
                (await Should.ThrowAsync<PostgresException>(() => ScalarAsync(c, $"SELECT count(*) FROM {peer.SchemaName}.item")))
                    .SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
                (await Should.ThrowAsync<PostgresException>(() => ScalarAsync(c, $"INSERT INTO {peer.SchemaName}.item VALUES (9, 'bad')")))
                    .SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
                (await Should.ThrowAsync<PostgresException>(() => ScalarAsync(c, "SELECT count(*) FROM socalytics_migrations.history")))
                    .SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
            }
        }
    }

    [Fact]
    public async Task RuntimeRoleCannotCreateObjectsAndFailedAccessCommitsNothing()
    {
        await using var provider = await SetUpAsync();
        var club = PersistenceModuleKey.Club;
        var registry = PersistenceModuleKey.Registry;

        await using (var c = await OpenAsync(provider, club))
        {
            await using var tx = await c.BeginTransactionAsync();
            await ScalarAsync(c, "INSERT INTO club.item VALUES (5, 'pending')", tx);
            await Should.ThrowAsync<PostgresException>(() =>
                ScalarAsync(c, $"INSERT INTO {registry.SchemaName}.item VALUES (5, 'bad')", tx));
            await tx.RollbackAsync();
        }

        await using (var c = await OpenAsync(provider, club))
        {
            (await ScalarAsync(c, "SELECT count(*) FROM club.item")).ShouldBe(0L);
            (await Should.ThrowAsync<PostgresException>(() => ScalarAsync(c, "CREATE TABLE club.extra (id int)")))
                .SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        }

        await using var r = await OpenAsync(provider, registry);
        (await ScalarAsync(r, "SELECT count(*) FROM registry.item")).ShouldBe(0L);
    }

    [Fact]
    public async Task PooledConnectionsDoNotLeakRoleBetweenModules()
    {
        await using var provider = await SetUpAsync();

        for (var i = 0; i < 3; i++)
        {
            foreach (var m in PersistenceModuleKey.All)
            {
                await using var c = await OpenAsync(provider, m);
                (await ScalarAsync(c, "SELECT current_user")).ShouldBe(m.RuntimeRoleName);
            }
        }
    }

    private sealed class Contributor(PersistenceModuleKey module) : IMigrationContributor
    {
        public PersistenceModuleKey Module => module;

        public IReadOnlyList<MigrationDescriptor> GetMigrations() => [Schema(module)];
    }
}
