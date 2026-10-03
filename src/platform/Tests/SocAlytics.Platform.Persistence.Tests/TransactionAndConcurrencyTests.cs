#pragma warning disable xUnit1051 // Cancellation is exercised explicitly with dedicated tokens.
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using Xunit;

namespace SocAlytics.Platform.Persistence.Tests;

public sealed class TransactionAndConcurrencyTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static readonly PersistenceModule Club = PersistenceModule.Club;

    [Fact]
    public async Task Successful_operation_commits_all_changes_together()
    {
        await using var host = await MigrateAsync();

        var result = await host.Factory.ExecuteInTransactionAsync(async (connection, transaction, token) =>
        {
            await connection.ExecuteAsync(Command("INSERT INTO club.item (id, n) VALUES (1, 10), (2, 20)", transaction, token));
            await connection.ExecuteAsync(Command("INSERT INTO club.item (id, n) VALUES (3, 30)", transaction, token));
            return "done";
        });

        result.ShouldBe("done");
        (await CountAsync(host)).ShouldBe(3);
    }

    [Fact]
    public async Task Exception_rolls_back_and_connection_is_reusable_without_an_active_transaction()
    {
        await using var host = await MigrateAsync();

        await Should.ThrowAsync<InvalidOperationException>(() => host.Factory.ExecuteInTransactionAsync(async (connection, transaction, token) =>
        {
            await connection.ExecuteAsync(Command("INSERT INTO club.item (id, n) VALUES (1, 10)", transaction, token));
            throw new InvalidOperationException("boom");
        }));

        (await CountAsync(host)).ShouldBe(0);
        await AssertPooledConnectionIsCleanAsync(host);
    }

    [Fact]
    public async Task Database_error_rolls_back_earlier_work()
    {
        await using var host = await MigrateAsync();

        await Should.ThrowAsync<PostgresException>(() => host.Factory.ExecuteInTransactionAsync(async (connection, transaction, token) =>
        {
            await connection.ExecuteAsync(Command("INSERT INTO club.item (id, n) VALUES (1, 10)", transaction, token));
            await connection.ExecuteAsync(Command("INSERT INTO club.item (id, n) VALUES (1, 11)", transaction, token));
        }));

        (await CountAsync(host)).ShouldBe(0);
        await AssertPooledConnectionIsCleanAsync(host);
    }

    [Fact]
    public async Task Cancellation_rolls_back_and_connection_is_reusable()
    {
        await using var host = await MigrateAsync();
        using var cancellation = new CancellationTokenSource();

        await Should.ThrowAsync<OperationCanceledException>(() => host.Factory.ExecuteInTransactionAsync(async (connection, transaction, token) =>
        {
            await connection.ExecuteAsync(Command("INSERT INTO club.item (id, n) VALUES (1, 10)", transaction, token));
            await cancellation.CancelAsync();
            token.ThrowIfCancellationRequested();
        }, cancellation.Token));

        (await CountAsync(host)).ShouldBe(0);
        await AssertPooledConnectionIsCleanAsync(host);
    }

    [Fact]
    public async Task Cancellation_of_a_running_command_rolls_back_and_connection_is_reusable()
    {
        await using var host = await MigrateAsync();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

        await Should.ThrowAsync<Exception>(() => host.Factory.ExecuteInTransactionAsync(async (connection, transaction, token) =>
        {
            await connection.ExecuteAsync(Command("INSERT INTO club.item (id, n) VALUES (1, 10)", transaction, token));
            await connection.ExecuteAsync(Command("SELECT pg_sleep(30)", transaction, token));
        }, cancellation.Token));

        (await CountAsync(host)).ShouldBe(0);
        await AssertPooledConnectionIsCleanAsync(host);
    }

    [Fact]
    public async Task Matching_version_update_changes_state_and_advances_version()
    {
        await using var host = await MigrateAsync();
        await SeedAsync(host);

        await host.Factory.ExecuteInTransactionAsync(async (connection, transaction, token) =>
        {
            OptimisticConcurrency.EnsureUpdated(await UpdateAsync(connection, transaction, token, newValue: 99, expectedVersion: 5));
        });

        (await ReadAsync(host)).ShouldBe((99, 6L));
    }

    [Fact]
    public async Task Stale_version_reports_conflict_and_commits_no_part_of_the_change()
    {
        await using var host = await MigrateAsync();
        await SeedAsync(host);

        await Should.ThrowAsync<ConcurrencyConflictException>(() => host.Factory.ExecuteInTransactionAsync(async (connection, transaction, token) =>
        {
            await connection.ExecuteAsync(Command("INSERT INTO club.item (id, n, version) VALUES (2, 7, 0)", transaction, token));
            OptimisticConcurrency.EnsureUpdated(await UpdateAsync(connection, transaction, token, newValue: 99, expectedVersion: 4));
        }));

        (await ReadAsync(host)).ShouldBe((10, 5L));
        (await CountAsync(host)).ShouldBe(1);
        await AssertPooledConnectionIsCleanAsync(host);
    }

    [Fact]
    public async Task Competing_writers_with_the_same_version_allow_exactly_one_to_succeed()
    {
        await using var host = await MigrateAsync();
        await SeedAsync(host);

        async Task<bool> TryWriteAsync(int value)
        {
            try
            {
                await host.Factory.ExecuteInTransactionAsync(async (connection, transaction, token) =>
                    OptimisticConcurrency.EnsureUpdated(await UpdateAsync(connection, transaction, token, value, expectedVersion: 5)));
                return true;
            }
            catch (ConcurrencyConflictException)
            {
                return false;
            }
        }

        var outcomes = await Task.WhenAll(TryWriteAsync(1), TryWriteAsync(2));

        outcomes.Count(o => o).ShouldBe(1);
        (await ReadAsync(host)).Version.ShouldBe(6L);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(2, true)]
    public void Affected_row_count_must_be_exactly_one(int affectedRows, bool throws)
    {
        if (throws)
        {
            Should.Throw<Exception>(() => OptimisticConcurrency.EnsureUpdated(affectedRows))
                .ShouldBeOfType(affectedRows == 0 ? typeof(ConcurrencyConflictException) : typeof(InvalidOperationException));
        }
        else
        {
            OptimisticConcurrency.EnsureUpdated(affectedRows);
        }
    }

    private static CommandDefinition Command(string sql, NpgsqlTransaction transaction, CancellationToken token) =>
        new(sql, transaction: transaction, cancellationToken: token);

    private static Task<int> UpdateAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken token, int newValue, long expectedVersion) =>
        connection.ExecuteAsync(new CommandDefinition(
            "UPDATE club.item SET n = @newValue, version = version + 1 WHERE id = 1 AND version = @expectedVersion",
            new { newValue, expectedVersion }, transaction, cancellationToken: token));

    private static async Task SeedAsync(Host host) =>
        await host.Factory.ExecuteInTransactionAsync(async (connection, transaction, token) =>
            await connection.ExecuteAsync(Command("INSERT INTO club.item (id, n, version) VALUES (1, 10, 5)", transaction, token)));

    private static async Task<(int Value, long Version)> ReadAsync(Host host)
    {
        await using var connection = await host.Factory.OpenConnectionAsync();
        return await connection.QuerySingleAsync<(int, long)>("SELECT n, version FROM club.item WHERE id = 1");
    }

    private static async Task<long> CountAsync(Host host)
    {
        await using var connection = await host.Factory.OpenConnectionAsync();
        return await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM club.item");
    }

    // The pool holds a single connection, so the next open must hand back the one used by the failed operation.
    private static async Task AssertPooledConnectionIsCleanAsync(Host host)
    {
        await using var connection = await host.Factory.OpenConnectionAsync();
        connection.FullState.ToString().ShouldNotContain("Broken");
        (await connection.ExecuteScalarAsync<bool>("SELECT pg_current_xact_id_if_assigned() IS NULL")).ShouldBeTrue();

        await using var transaction = await connection.BeginTransactionAsync();
        await connection.ExecuteAsync("INSERT INTO club.item (id, n) VALUES (1000, 1)", transaction: transaction);
        await transaction.RollbackAsync();
    }

    private async Task<Host> MigrateAsync()
    {
        var bootstrap = new NpgsqlConnectionStringBuilder(await postgres.CreateDatabaseAsync());
        var services = new ServiceCollection();
        services.AddSingleton(NpgsqlDataSource.Create(bootstrap.ConnectionString));
        services.AddModulePersistence(Club, new Contributor(Club,
            "CREATE SCHEMA club; CREATE TABLE club.item (id int PRIMARY KEY, n int NOT NULL, version bigint NOT NULL DEFAULT 0);"));

        var provider = services.BuildServiceProvider();
        await provider.GetRequiredService<IMigrationRunner>().RunAsync();

        // A single pooled connection proves that every failed operation leaves a reusable, clean connection.
        bootstrap.Pooling = true;
        bootstrap.MaxPoolSize = 1;
        var poolServices = new ServiceCollection();
        var dataSource = NpgsqlDataSource.Create(bootstrap.ConnectionString);
        poolServices.AddSingleton(dataSource);
        poolServices.AddModulePersistence(Club, new Contributor(Club, "SELECT 1;"));
        var poolProvider = poolServices.BuildServiceProvider();

        return new Host(provider, poolProvider, dataSource);
    }

    private sealed class Host(ServiceProvider migrationProvider, ServiceProvider poolProvider, NpgsqlDataSource dataSource) : IAsyncDisposable
    {
        public IModuleConnectionFactory Factory { get; } = poolProvider.GetRequiredKeyedService<IModuleConnectionFactory>(Club.Key);

        public async ValueTask DisposeAsync()
        {
            await poolProvider.DisposeAsync();
            await dataSource.DisposeAsync();
            await migrationProvider.DisposeAsync();
        }
    }

    private sealed class Contributor(PersistenceModule module, string sql) : IMigrationContributor
    {
        public PersistenceModule Module { get; } = module;

        public IEnumerable<MigrationDescriptor> GetMigrations() =>
            [new(Module, 1, "initial", System.Text.Encoding.UTF8.GetBytes(sql))];
    }
}
