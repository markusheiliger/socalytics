using System.Text;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using SocAlytics.Platform.Persistence;
using SocAlytics.Platform.Persistence.Connections;
using Testcontainers.PostgreSql;
using Xunit;

namespace SocAlytics.Platform.Persistence.Tests;

public sealed class ModuleRoleIsolationTests
{
    private const string RuntimeLogin = "socalytics_app";
    private const string InsufficientPrivilege = "42501";

    private static readonly ModuleKey[] Modules = Enum.GetValues<ModuleKey>();

    [Fact]
    public async Task EachModuleRoleOwnsOrAccessesOnlyItsSchema()
    {
        await using var database = await StartDatabase();
        await using var admin = new NpgsqlConnection(database.GetConnectionString());

        foreach (var module in Modules)
        {
            var schema = module.ToSchemaName();
            (await admin.ExecuteScalarAsync<string>(
                "SELECT nspowner::regrole::text FROM pg_namespace WHERE nspname = @schema", new { schema }))
                .ShouldBe(Owner(module));
            (await admin.ExecuteScalarAsync<string>(
                "SELECT relowner::regrole::text FROM pg_class WHERE oid = to_regclass(@table)", new { table = $"{schema}.probe" }))
                .ShouldBe(Owner(module));

            foreach (var peer in Modules)
            {
                var peerSchema = peer.ToSchemaName();
                var own = peer == module;
                (await HasSchemaPrivilege(admin, Runtime(module), peerSchema, "USAGE")).ShouldBe(own, $"{Runtime(module)} USAGE on {peerSchema}");
                (await HasSchemaPrivilege(admin, Runtime(module), peerSchema, "CREATE")).ShouldBeFalse();
                (await HasSchemaPrivilege(admin, Owner(module), peerSchema, "CREATE")).ShouldBe(own, $"{Owner(module)} CREATE on {peerSchema}");
                (await HasSchemaPrivilege(admin, Owner(module), peerSchema, "USAGE")).ShouldBe(own, $"{Owner(module)} USAGE on {peerSchema}");
                (await admin.ExecuteScalarAsync<bool>(
                    "SELECT has_table_privilege(@role, @table, 'SELECT, INSERT, UPDATE, DELETE')",
                    new { role = Runtime(module), table = $"{peerSchema}.probe" }))
                    .ShouldBe(own, $"{Runtime(module)} DML on {peerSchema}.probe");
            }

            (await HasSchemaPrivilege(admin, Owner(module), "socalytics_migrations", "USAGE")).ShouldBeFalse();
            (await HasSchemaPrivilege(admin, Runtime(module), "socalytics_migrations", "USAGE")).ShouldBeFalse();
            (await HasSchemaPrivilege(admin, RuntimeLogin, schema, "USAGE")).ShouldBeFalse();
        }
    }

    [Fact]
    public async Task ModuleSessionsRunAsTheModuleRuntimeRoleWithinItsSchema()
    {
        await using var database = await StartDatabase();
        var factory = ConnectionFactory(database);

        foreach (var module in Modules)
        {
            await using var connection = factory.CreateConnection(module);
            await connection.OpenAsync(TestContext.Current.CancellationToken);

            (await connection.ExecuteScalarAsync<string>("SELECT current_user")).ShouldBe(Runtime(module));
            (await connection.ExecuteScalarAsync<string>("SELECT session_user")).ShouldBe(RuntimeLogin);
            (await connection.ExecuteAsync("INSERT INTO probe VALUES (2)")).ShouldBe(1);
            (await connection.ExecuteAsync("UPDATE probe SET id = 3 WHERE id = 2")).ShouldBe(1);
            (await connection.ExecuteAsync("DELETE FROM probe WHERE id = 3")).ShouldBe(1);
            (await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM probe")).ShouldBe(1);
            await ShouldBeDenied(() => connection.ExecuteAsync("CREATE TABLE runtime_created (id integer)"));
        }
    }

    [Fact]
    public async Task CrossSchemaReadsAndWritesFailWithoutCommittingChanges()
    {
        await using var database = await StartDatabase();
        var factory = ConnectionFactory(database);

        foreach (var module in Modules)
        {
            foreach (var peer in Modules.Where(peer => peer != module))
            {
                var peerTable = $"{peer.ToSchemaName()}.probe";
                await using var connection = factory.CreateConnection(module);
                await connection.OpenAsync(TestContext.Current.CancellationToken);

                await ShouldBeDenied(() => connection.QueryAsync<int>($"SELECT id FROM {peerTable}"));
                await ShouldBeDenied(() => connection.ExecuteAsync($"INSERT INTO {peerTable} VALUES (9)"));
                await ShouldBeDenied(() => connection.ExecuteAsync($"UPDATE {peerTable} SET id = 9"));
                await ShouldBeDenied(() => connection.ExecuteAsync($"DELETE FROM {peerTable}"));
                await ShouldBeDenied(() => connection.ExecuteAsync($"CREATE TABLE {peer.ToSchemaName()}.intruder (id integer)"));

                await using (var transaction = await connection.BeginTransactionAsync(TestContext.Current.CancellationToken))
                {
                    await connection.ExecuteAsync("INSERT INTO probe VALUES (7)", transaction: transaction);
                    await ShouldBeDenied(() => connection.ExecuteAsync($"INSERT INTO {peerTable} VALUES (7)", transaction: transaction));
                    // PostgreSQL turns COMMIT of an aborted transaction into ROLLBACK, discarding the owned insert too.
                    await transaction.CommitAsync(TestContext.Current.CancellationToken);
                }
            }
        }

        await using var admin = new NpgsqlConnection(database.GetConnectionString());
        foreach (var module in Modules)
        {
            var schema = module.ToSchemaName();
            (await admin.QueryAsync<int>($"SELECT id FROM {schema}.probe")).ShouldBe([1]);
            (await admin.ExecuteScalarAsync<string?>("SELECT to_regclass(@table)::text", new { table = $"{schema}.intruder" })).ShouldBeNull();
        }
    }

    [Fact]
    public async Task MigrationsRunAsTheOwningModuleOwnerAndCannotAlterPeerSchemas()
    {
        await using var database = await StartDatabase();
        var intruding = new MigrationDescriptor(
            ModuleKey.Club, 2, "intrude", Encoding.UTF8.GetBytes("CREATE TABLE registry.intruder (id integer);"));

        Should.Throw<InvalidOperationException>(() =>
            Provider(database, Contributors(ModuleKey.Club, intruding)).GetRequiredService<MigrationOrchestrator>().Run());

        await using var admin = new NpgsqlConnection(database.GetConnectionString());
        (await admin.ExecuteScalarAsync<string?>("SELECT to_regclass('registry.intruder')::text")).ShouldBeNull();
        (await admin.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM socalytics_migrations.history WHERE script_identity = 'intrude'")).ShouldBe(0);
    }

    private static async Task<PostgreSqlContainer> StartDatabase()
    {
        var database = new PostgreSqlBuilder("postgres:17-alpine").Build();
        await database.StartAsync();

        await using var admin = new NpgsqlConnection(database.GetConnectionString());
        await admin.ExecuteAsync($"CREATE ROLE {RuntimeLogin} LOGIN NOINHERIT PASSWORD 'runtime'");
        Provider(database, Contributors()).GetRequiredService<MigrationOrchestrator>().Run();
        return database;
    }

    private static IModuleConnectionFactory ConnectionFactory(PostgreSqlContainer database) =>
        Provider(database, Contributors()).GetRequiredService<IModuleConnectionFactory>();

    private static ServiceProvider Provider(PostgreSqlContainer database, IEnumerable<IModuleMigrationContributor> contributors)
    {
        var runtime = new NpgsqlConnectionStringBuilder(database.GetConnectionString())
        {
            Username = RuntimeLogin,
            Password = "runtime"
        };

        var services = new ServiceCollection();
        services.AddPlatformPersistence(options =>
        {
            options.BootstrapConnectionString = database.GetConnectionString();
            options.RuntimeConnectionString = runtime.ConnectionString;
        });
        foreach (var contributor in contributors)
        {
            services.AddSingleton(contributor);
        }

        return services.BuildServiceProvider();
    }

    private static IEnumerable<IModuleMigrationContributor> Contributors(ModuleKey? extraModule = null, MigrationDescriptor? extra = null) =>
        Modules.Select(module => (IModuleMigrationContributor)new Contributor(
            module,
            [
                new MigrationDescriptor(module, 1, "probe", Encoding.UTF8.GetBytes(
                    $"CREATE TABLE {module.ToSchemaName()}.probe (id integer NOT NULL); INSERT INTO {module.ToSchemaName()}.probe VALUES (1);")),
                .. module == extraModule && extra is not null ? [extra] : Array.Empty<MigrationDescriptor>()
            ]));

    private static Task<bool> HasSchemaPrivilege(NpgsqlConnection connection, string role, string schema, string privilege) =>
        connection.ExecuteScalarAsync<bool>("SELECT has_schema_privilege(@role, @schema, @privilege)", new { role, schema, privilege });

    private static async Task ShouldBeDenied(Func<Task> action)
    {
        var error = await Should.ThrowAsync<PostgresException>(action);
        error.SqlState.ShouldBe(InsufficientPrivilege);
    }

    private static string Owner(ModuleKey module) => $"socalytics_{module.ToSchemaName()}_owner";

    private static string Runtime(ModuleKey module) => $"socalytics_{module.ToSchemaName()}_runtime";

    private sealed class Contributor(ModuleKey moduleKey, MigrationDescriptor[] scripts) : IModuleMigrationContributor
    {
        public ModuleKey ModuleKey => moduleKey;
        public IReadOnlyList<MigrationDescriptor> GetMigrations() => scripts;
    }
}
