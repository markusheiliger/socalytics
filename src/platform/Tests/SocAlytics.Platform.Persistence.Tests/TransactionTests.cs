using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using Testcontainers.PostgreSql;
using Xunit;

namespace SocAlytics.Platform.Persistence.Tests;

public sealed class TransactionTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine").Build();
    private ServiceProvider _provider = null!;
    private string _connectionString = null!;
    private IModuleConnectionFactory<ClubMarker> _factory = null!;

    private sealed class ClubMarker;

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
            await admin.ExecuteAsync($"CREATE DATABASE {name}");
        }

        _connectionString = new NpgsqlConnectionStringBuilder(_container.GetConnectionString()) { Database = name }.ConnectionString;
        var services = new ServiceCollection();
        services.AddPlatformPersistence(o => o.ConnectionString = _connectionString);
        services.AddModulePersistence<ClubMarker>(new Contributor(ModuleKey.Club));
        _provider = services.BuildServiceProvider();
        await _provider.GetRequiredService<MigrationRunner>().RunAsync(TestContext.Current.CancellationToken);
        _factory = _provider.GetRequiredService<IModuleConnectionFactory<ClubMarker>>();

        await using var bootstrap = new NpgsqlConnection(_connectionString);
        await bootstrap.OpenAsync(TestContext.Current.CancellationToken);
        await bootstrap.ExecuteAsync($"""
            SET ROLE "{ModuleKey.Club.OwnerRole}";
            CREATE TABLE club.item (id int PRIMARY KEY, label text, version bigint NOT NULL);
            INSERT INTO club.item VALUES (1, 'seed', 1);
            RESET ROLE;
            """);
    }

    public async ValueTask DisposeAsync()
    {
        await _provider.DisposeAsync();
        await _container.DisposeAsync();
    }

    private async Task<(string Label, long Version)[]> RowsAsync()
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        var rows = await connection.QueryAsync<(string, long)>("SELECT label, version FROM club.item ORDER BY id");
        return rows.ToArray();
    }

    private static Task<int> UpdateAsync(NpgsqlConnection c, NpgsqlTransaction t, string label, long expected) =>
        c.ExecuteAsync(
            "UPDATE club.item SET label = @label, version = version + 1 WHERE id = 1 AND version = @expected",
            new { label, expected }, t);

    [Fact]
    public async Task SuccessfulOperationCommitsAllChangesTogether()
    {
        await _factory.ExecuteInTransactionAsync(async (c, t, _) =>
        {
            await c.ExecuteAsync("INSERT INTO club.item VALUES (2, 'a', 1)", transaction: t);
            await c.ExecuteAsync("INSERT INTO club.item VALUES (3, 'b', 1)", transaction: t);
        }, TestContext.Current.CancellationToken);

        (await RowsAsync()).Length.ShouldBe(3);
    }

    [Fact]
    public async Task ExceptionRollsBackEverythingAndPoolStaysClean()
    {
        await Should.ThrowAsync<InvalidOperationException>(() => _factory.ExecuteInTransactionAsync(async (c, t, _) =>
        {
            await c.ExecuteAsync("INSERT INTO club.item VALUES (2, 'a', 1)", transaction: t);
            throw new InvalidOperationException("boom");
        }, TestContext.Current.CancellationToken));

        (await RowsAsync()).Length.ShouldBe(1);
        await AssertReusableAsync();
    }

    [Fact]
    public async Task DatabaseErrorRollsBackEarlierWork()
    {
        await Should.ThrowAsync<PostgresException>(() => _factory.ExecuteInTransactionAsync(async (c, t, _) =>
        {
            await c.ExecuteAsync("INSERT INTO club.item VALUES (2, 'a', 1)", transaction: t);
            await c.ExecuteAsync("INSERT INTO club.item VALUES (1, 'dup', 1)", transaction: t);
        }, TestContext.Current.CancellationToken));

        (await RowsAsync()).Length.ShouldBe(1);
        await AssertReusableAsync();
    }

    [Fact]
    public async Task CancellationRollsBackAndPoolStaysClean()
    {
        using var cts = new CancellationTokenSource();
        await Should.ThrowAsync<OperationCanceledException>(() => _factory.ExecuteInTransactionAsync(async (c, t, token) =>
        {
            await c.ExecuteAsync("INSERT INTO club.item VALUES (2, 'a', 1)", transaction: t);
            await cts.CancelAsync();
            token.ThrowIfCancellationRequested();
        }, cts.Token));

        (await RowsAsync()).Length.ShouldBe(1);
        await AssertReusableAsync();
    }

    [Fact]
    public async Task ResultIsReturnedAfterCommit()
    {
        var result = await _factory.ExecuteInTransactionAsync(
            async (c, t, _) => await c.ExecuteScalarAsync<int>("SELECT 42", transaction: t),
            TestContext.Current.CancellationToken);

        result.ShouldBe(42);
    }

    [Fact]
    public async Task MatchingVersionUpdatesAndAdvancesAtomically()
    {
        await _factory.ExecuteInTransactionAsync(
            async (c, t, _) => OptimisticConcurrency.EnsureApplied(await UpdateAsync(c, t, "changed", 1)),
            TestContext.Current.CancellationToken);

        (await RowsAsync()).ShouldBe([("changed", 2L)]);
    }

    [Fact]
    public async Task StaleVersionReportsConflictAndCommitsNothing()
    {
        await Should.ThrowAsync<ConcurrencyConflictException>(() => _factory.ExecuteInTransactionAsync(async (c, t, _) =>
        {
            await c.ExecuteAsync("INSERT INTO club.item VALUES (2, 'partial', 1)", transaction: t);
            OptimisticConcurrency.EnsureApplied(await UpdateAsync(c, t, "stale", 99));
        }, TestContext.Current.CancellationToken));

        (await RowsAsync()).ShouldBe([("seed", 1L)]);
    }

    [Fact]
    public async Task SecondWriterWithOldVersionConflicts()
    {
        await _factory.ExecuteInTransactionAsync(
            async (c, t, _) => OptimisticConcurrency.EnsureApplied(await UpdateAsync(c, t, "first", 1)),
            TestContext.Current.CancellationToken);

        await Should.ThrowAsync<ConcurrencyConflictException>(() => _factory.ExecuteInTransactionAsync(
            async (c, t, _) => OptimisticConcurrency.EnsureApplied(await UpdateAsync(c, t, "second", 1)),
            TestContext.Current.CancellationToken));

        (await RowsAsync()).ShouldBe([("first", 2L)]);
    }

    [Fact]
    public void AffectedRowCountIsValidated()
    {
        OptimisticConcurrency.IsApplied(1).ShouldBeTrue();
        OptimisticConcurrency.IsApplied(0).ShouldBeFalse();
        Should.Throw<InvalidOperationException>(() => OptimisticConcurrency.IsApplied(2));
    }

    private async Task AssertReusableAsync()
    {
        await using var connection = await _factory.OpenAsync(TestContext.Current.CancellationToken);
        var state = await connection.QuerySingleAsync<(string, string)>(
            "SELECT current_user::text, CASE WHEN pg_current_xact_id_if_assigned() IS NULL THEN 'clean' ELSE 'dirty' END");
        state.ShouldBe((ModuleKey.Club.RuntimeRole, "clean"));
        connection.State.ShouldBe(System.Data.ConnectionState.Open);
        await _factory.ExecuteInTransactionAsync(
            (c, t, _) => c.ExecuteAsync("UPDATE club.item SET label = label", transaction: t),
            TestContext.Current.CancellationToken);
    }
}
