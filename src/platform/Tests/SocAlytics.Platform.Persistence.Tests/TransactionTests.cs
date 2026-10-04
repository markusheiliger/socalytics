using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using System.Text;
using Testcontainers.PostgreSql;
using Xunit;

namespace SocAlytics.Platform.Persistence.Tests;

public sealed class TransactionTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine").Build();
    private ServiceProvider _provider = null!;
    private IModuleConnectionFactory _factory = null!;

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync(TestContext.Current.CancellationToken);
        var module = PersistenceModuleKey.Club;
        var sql = $"""
            CREATE SCHEMA {module.Schema};
            GRANT USAGE ON SCHEMA {module.Schema} TO {module.RuntimeRole};
            CREATE TABLE {module.Schema}.items (id int PRIMARY KEY, value text, version bigint NOT NULL DEFAULT 1);
            GRANT SELECT, INSERT, UPDATE ON {module.Schema}.items TO {module.RuntimeRole};
            """;
        _provider = new ServiceCollection()
            .AddPlatformPersistence(_container.GetConnectionString())
            .AddModulePersistence(new Contributor(module, new MigrationDescriptor(module, 1, "0001", Encoding.UTF8.GetBytes(sql))))
            .BuildServiceProvider();
        await _provider.GetRequiredService<MigrationOrchestrator>().MigrateAsync(TestContext.Current.CancellationToken);
        _factory = _provider.GetRequiredKeyedService<IModuleConnectionFactory>(module.Key);
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

    private async Task<long> CountAsync()
    {
        await using var connection = await _factory.OpenConnectionAsync(TestContext.Current.CancellationToken);
        return await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM club.items");
    }

    private static Task InsertAsync(NpgsqlConnection c, NpgsqlTransaction t, int id) =>
        c.ExecuteAsync("INSERT INTO club.items (id, value) VALUES (@id, 'x')", new { id }, t);

    [Fact]
    public async Task SuccessfulOperationCommitsAllWork()
    {
        await _factory.ExecuteInTransactionAsync(async (c, t, _) =>
        {
            await InsertAsync(c, t, 1);
            await InsertAsync(c, t, 2);
        }, TestContext.Current.CancellationToken);

        (await CountAsync()).ShouldBe(2);
    }

    [Fact]
    public async Task ExceptionRollsBackAndConnectionIsReusableAndClean()
    {
        await Should.ThrowAsync<InvalidOperationException>(() => _factory.ExecuteInTransactionAsync(async (c, t, _) =>
        {
            await InsertAsync(c, t, 10);
            throw new InvalidOperationException("boom");
        }, TestContext.Current.CancellationToken));

        (await CountAsync()).ShouldBe(0);
        await using var connection = await _factory.OpenConnectionAsync(TestContext.Current.CancellationToken);
        (await connection.ExecuteScalarAsync<bool>("SELECT pg_current_xact_id_if_assigned() IS NULL")).ShouldBeTrue();
        await using var tx = await connection.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await InsertAsync(connection, tx, 11);
        await tx.CommitAsync(TestContext.Current.CancellationToken);
        (await CountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task CancellationRollsBackWork()
    {
        using var cts = new CancellationTokenSource();
        await Should.ThrowAsync<OperationCanceledException>(() => _factory.ExecuteInTransactionAsync(async (c, t, ct) =>
        {
            await InsertAsync(c, t, 20);
            await cts.CancelAsync();
            await c.ExecuteScalarAsync<int>(new CommandDefinition("SELECT 1", transaction: t, cancellationToken: ct));
        }, cts.Token));

        (await CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task MatchingVersionUpdateAdvancesVersion()
    {
        await _factory.ExecuteInTransactionAsync(async (c, t, _) => await InsertAsync(c, t, 30), TestContext.Current.CancellationToken);

        await _factory.ExecuteInTransactionAsync(async (c, t, _) =>
            OptimisticConcurrency.EnsureUpdated(await c.ExecuteAsync(
                "UPDATE club.items SET value = 'y', version = version + 1 WHERE id = 30 AND version = @v", new { v = 1L }, t)),
            TestContext.Current.CancellationToken);

        await using var connection = await _factory.OpenConnectionAsync(TestContext.Current.CancellationToken);
        (await connection.ExecuteScalarAsync<long>("SELECT version FROM club.items WHERE id = 30")).ShouldBe(2);
    }

    [Fact]
    public async Task StaleVersionConflictsWithoutPartialCommit()
    {
        await _factory.ExecuteInTransactionAsync(async (c, t, _) => await InsertAsync(c, t, 40), TestContext.Current.CancellationToken);

        await Should.ThrowAsync<ConcurrencyConflictException>(() => _factory.ExecuteInTransactionAsync(async (c, t, _) =>
        {
            await InsertAsync(c, t, 41);
            OptimisticConcurrency.EnsureUpdated(await c.ExecuteAsync(
                "UPDATE club.items SET value = 'z', version = version + 1 WHERE id = 40 AND version = @v", new { v = 99L }, t));
        }, TestContext.Current.CancellationToken));

        await using var connection = await _factory.OpenConnectionAsync(TestContext.Current.CancellationToken);
        (await connection.ExecuteScalarAsync<string>("SELECT string_agg(id || ':' || value || ':' || version, ',') FROM club.items"))
            .ShouldBe("40:x:1");
    }

    [Fact]
    public void AffectedRowCountsAreValidated()
    {
        OptimisticConcurrency.EnsureUpdated(1);
        Should.Throw<ConcurrencyConflictException>(() => OptimisticConcurrency.EnsureUpdated(0));
        Should.Throw<InvalidOperationException>(() => OptimisticConcurrency.EnsureUpdated(2));
    }
}
