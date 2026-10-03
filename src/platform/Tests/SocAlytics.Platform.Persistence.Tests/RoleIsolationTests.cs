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

    private sealed class ClubMarker;
    private sealed class RecordingsMarker;

    private sealed class Contributor(ModuleKey module) : IModuleMigrationContributor
    {
        public ModuleKey Module { get; } = module;
        public IReadOnlyList<MigrationDescriptor> Migrations => [];
    }

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();
        var name = "db_" + Guid.NewGuid().ToString("N");
        await using (var admin = new NpgsqlConnection(_container.GetConnectionString()))
        {
            await admin.OpenAsync(TestContext.Current.CancellationToken);
            await ExecAsync(admin, $"CREATE DATABASE {name}");
        }

        _connectionString = new NpgsqlConnectionStringBuilder(_container.GetConnectionString()) { Database = name }.ConnectionString;
        var services = new ServiceCollection();
        services.AddPlatformPersistence(o => o.ConnectionString = _connectionString);
        services.AddModulePersistence<ClubMarker>(new Contributor(ModuleKey.Club));
        services.AddModulePersistence<RecordingsMarker>(new Contributor(ModuleKey.Recordings));
        _provider = services.BuildServiceProvider();
        await _provider.GetRequiredService<MigrationRunner>().RunAsync(TestContext.Current.CancellationToken);

        // Disposable objects created by each owner role stand in for module tables.
        await using var bootstrap = new NpgsqlConnection(_connectionString);
        await bootstrap.OpenAsync(TestContext.Current.CancellationToken);
        foreach (var module in new[] { ModuleKey.Club, ModuleKey.Recordings })
        {
            await ExecAsync(bootstrap, $"""
                SET ROLE "{module.OwnerRole}";
                CREATE TABLE "{module.Schema}".item (id int PRIMARY KEY, label text);
                INSERT INTO "{module.Schema}".item VALUES (1, 'seed');
                RESET ROLE;
                """);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _provider.DisposeAsync();
        await _container.DisposeAsync();
    }

    private static async Task ExecAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private async Task<long> CountAsync(string schema)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand($"SELECT count(*) FROM \"{schema}\".item", connection);
        return (long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    [Fact]
    public async Task EachSchemaIsOwnedByItsModuleOwnerRole()
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT nspname, pg_get_userbyid(nspowner) FROM pg_namespace WHERE nspname = ANY(@names)", connection);
        command.Parameters.AddWithValue("names", ModuleKey.All.Select(m => m.Schema).ToArray());
        var owners = new Dictionary<string, string>();
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            owners[reader.GetString(0)] = reader.GetString(1);
        }

        owners.Count.ShouldBe(ModuleKey.All.Count);
        foreach (var module in ModuleKey.All)
        {
            owners[module.Schema].ShouldBe(module.OwnerRole);
        }
    }

    [Fact]
    public async Task RolesAreNoLoginAndBootstrapIsRepeatable()
    {
        await _provider.GetRequiredService<MigrationRunner>().RunAsync(TestContext.Current.CancellationToken);

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM pg_roles WHERE rolname = ANY(@names) AND NOT rolcanlogin", connection);
        command.Parameters.AddWithValue("names", ModuleKey.All.SelectMany(m => new[] { m.OwnerRole, m.RuntimeRole }).ToArray());
        ((long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!).ShouldBe(ModuleKey.All.Count * 2);
    }

    [Fact]
    public async Task RuntimeSessionReadsAndWritesOwnSchemaOnly()
    {
        var factory = _provider.GetRequiredService<IModuleConnectionFactory<ClubMarker>>();
        await using var connection = await factory.OpenAsync(TestContext.Current.CancellationToken);

        await ExecAsync(connection, "INSERT INTO club.item VALUES (2, 'own')");
        await using var command = new NpgsqlCommand("SELECT current_user, (SELECT count(*) FROM club.item)", connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        (await reader.ReadAsync(TestContext.Current.CancellationToken)).ShouldBeTrue();
        reader.GetString(0).ShouldBe(ModuleKey.Club.RuntimeRole);
        reader.GetInt64(1).ShouldBe(2);
    }

    [Theory]
    [InlineData("SELECT * FROM recordings.item")]
    [InlineData("INSERT INTO recordings.item VALUES (9, 'x')")]
    [InlineData("UPDATE recordings.item SET label = 'x'")]
    [InlineData("DELETE FROM recordings.item")]
    [InlineData("CREATE TABLE recordings.intruder (id int)")]
    [InlineData("CREATE TABLE club.intruder (id int)")]
    [InlineData("SELECT * FROM socalytics_migrations.history")]
    public async Task CrossSchemaAndDdlAccessIsDenied(string sql)
    {
        var factory = _provider.GetRequiredService<IModuleConnectionFactory<ClubMarker>>();
        await using var connection = await factory.OpenAsync(TestContext.Current.CancellationToken);

        var ex = await Should.ThrowAsync<PostgresException>(() => ExecAsync(connection, sql));
        ex.SqlState.ShouldBe("42501");
    }

    [Fact]
    public async Task FailedAccessCommitsNothing()
    {
        var factory = _provider.GetRequiredService<IModuleConnectionFactory<ClubMarker>>();
        await using (var connection = await factory.OpenAsync(TestContext.Current.CancellationToken))
        {
            await using var transaction = await connection.BeginTransactionAsync(TestContext.Current.CancellationToken);
            await ExecAsync(connection, "INSERT INTO club.item VALUES (3, 'pending')");
            await Should.ThrowAsync<PostgresException>(() => ExecAsync(connection, "INSERT INTO recordings.item VALUES (3, 'x')"));
            await transaction.RollbackAsync(TestContext.Current.CancellationToken);
        }

        (await CountAsync("club")).ShouldBe(1);
        (await CountAsync("recordings")).ShouldBe(1);
    }

    [Fact]
    public async Task PooledConnectionsDoNotLeakAnotherModulesRole()
    {
        var club = _provider.GetRequiredService<IModuleConnectionFactory<ClubMarker>>();
        var recordings = _provider.GetRequiredService<IModuleConnectionFactory<RecordingsMarker>>();
        for (var i = 0; i < 3; i++)
        {
            await using (await club.OpenAsync(TestContext.Current.CancellationToken)) { }

            await using var connection = await recordings.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = new NpgsqlCommand("SELECT current_user", connection);
            ((string)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!).ShouldBe(ModuleKey.Recordings.RuntimeRole);
            await ExecAsync(connection, "SELECT 1 FROM recordings.item");
            await Should.ThrowAsync<PostgresException>(() => ExecAsync(connection, "SELECT 1 FROM club.item"));
        }
    }
}
