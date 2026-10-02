using System.Text;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using Xunit;

namespace SocAlytics.Platform.Persistence.Tests;

public sealed class TransactionTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static readonly PersistenceModuleKey Module = PersistenceModuleKey.Club;

    private async Task<ServiceProvider> SetUpAsync()
    {
        var cs = await postgres.CreateDatabaseAsync();
        var services = new ServiceCollection();
        services.AddPlatformPersistence(o => o.BootstrapConnectionString = cs);
        services.AddModuleMigrations(new Contributor());
        services.AddModulePersistence(Module);
        var provider = services.BuildServiceProvider();
        await provider.GetRequiredService<IMigrationRunner>().MigrateAsync();
        return provider;
    }

    private static IModuleTransactionExecutor Executor(ServiceProvider p) =>
        p.GetRequiredKeyedService<IModuleTransactionExecutor>(Module);

    private static async Task<long> CountAsync(ServiceProvider p)
    {
        await using var c = await p.GetRequiredKeyedService<IModuleConnectionFactory>(Module).OpenAsync();
        return await c.ExecuteScalarAsync<long>("SELECT count(*) FROM club.doc");
    }

    [Fact]
    public async Task CommitsAllStatementsAtomically()
    {
        await using var provider = await SetUpAsync();

        var result = await Executor(provider).ExecuteAsync(async (c, tx, ct) =>
        {
            await c.ExecuteAsync(new CommandDefinition("INSERT INTO club.doc (id) VALUES (1), (2)", transaction: tx, cancellationToken: ct));
            return 42;
        });

        result.ShouldBe(42);
        (await CountAsync(provider)).ShouldBe(2);
    }

    [Fact]
    public async Task ExceptionRollsBackAndRethrowsOriginal()
    {
        await using var provider = await SetUpAsync();

        await Should.ThrowAsync<ArgumentException>(() => Executor(provider).ExecuteAsync(async (c, tx, ct) =>
        {
            await c.ExecuteAsync(new CommandDefinition("INSERT INTO club.doc (id) VALUES (1)", transaction: tx, cancellationToken: ct));
            throw new ArgumentException("boom");
        }));

        (await CountAsync(provider)).ShouldBe(0);
    }

    [Fact]
    public async Task CancellationRollsBack()
    {
        await using var provider = await SetUpAsync();
        using var cts = new CancellationTokenSource();

        await Should.ThrowAsync<OperationCanceledException>(() => Executor(provider).ExecuteAsync(async (c, tx, ct) =>
        {
            await c.ExecuteAsync(new CommandDefinition("INSERT INTO club.doc (id) VALUES (1)", transaction: tx, cancellationToken: ct));
            await cts.CancelAsync();
            ct.ThrowIfCancellationRequested();
        }, cts.Token));

        (await CountAsync(provider)).ShouldBe(0);
    }

    [Fact]
    public async Task ConnectionsRemainReusableAndCleanAfterFailures()
    {
        await using var provider = await SetUpAsync();
        var executor = Executor(provider);

        for (var i = 0; i < 5; i++)
        {
            await Should.ThrowAsync<PostgresException>(() => executor.ExecuteAsync(async (c, tx, ct) =>
            {
                await c.ExecuteAsync(new CommandDefinition("INSERT INTO club.doc (id) VALUES (1)", transaction: tx, cancellationToken: ct));
                await c.ExecuteAsync(new CommandDefinition("INSERT INTO club.doc (id) VALUES (1)", transaction: tx, cancellationToken: ct));
            }));
        }

        var state = await executor.ExecuteAsync(async (c, tx, ct) =>
        {
            var user = await c.ExecuteScalarAsync<string>(new CommandDefinition("SELECT current_user", transaction: tx, cancellationToken: ct));
            await c.ExecuteAsync(new CommandDefinition("INSERT INTO club.doc (id) VALUES (7)", transaction: tx, cancellationToken: ct));
            return user;
        });

        state.ShouldBe(Module.RuntimeRoleName);
        (await CountAsync(provider)).ShouldBe(1);
    }

    private const string GuardedUpdate = "UPDATE club.doc SET body = @body, version = version + 1 WHERE id = @id AND version = @expected";

    [Fact]
    public async Task MatchingVersionUpdateIncrementsVersion()
    {
        await using var provider = await SetUpAsync();
        var executor = Executor(provider);
        await executor.ExecuteAsync(async (c, tx, ct) =>
            await c.ExecuteAsync(new CommandDefinition("INSERT INTO club.doc (id) VALUES (1)", transaction: tx, cancellationToken: ct)));

        await executor.ExecuteAsync(async (c, tx, ct) =>
            OptimisticConcurrency.EnsureUpdated(await c.ExecuteAsync(
                new CommandDefinition(GuardedUpdate, new { id = 1, expected = 0L, body = "a" }, tx, cancellationToken: ct))));

        await using var read = await provider.GetRequiredKeyedService<IModuleConnectionFactory>(Module).OpenAsync();
        (await read.ExecuteScalarAsync<long>("SELECT version FROM club.doc WHERE id = 1")).ShouldBe(1L);
    }

    [Fact]
    public async Task StaleVersionConflictsAndCommitsNothing()
    {
        await using var provider = await SetUpAsync();
        var executor = Executor(provider);
        await executor.ExecuteAsync(async (c, tx, ct) =>
            await c.ExecuteAsync(new CommandDefinition("INSERT INTO club.doc (id, version) VALUES (1, 3)", transaction: tx, cancellationToken: ct)));

        await Should.ThrowAsync<ConcurrencyConflictException>(() => executor.ExecuteAsync(async (c, tx, ct) =>
        {
            await c.ExecuteAsync(new CommandDefinition("INSERT INTO club.doc (id) VALUES (2)", transaction: tx, cancellationToken: ct));
            OptimisticConcurrency.EnsureUpdated(await c.ExecuteAsync(
                new CommandDefinition(GuardedUpdate, new { id = 1, expected = 2L, body = "stale" }, tx, cancellationToken: ct)));
        }));

        await using var read = await provider.GetRequiredKeyedService<IModuleConnectionFactory>(Module).OpenAsync();
        (await read.ExecuteScalarAsync<long>("SELECT count(*) FROM club.doc")).ShouldBe(1L);
        (await read.ExecuteScalarAsync<long>("SELECT version FROM club.doc WHERE id = 1")).ShouldBe(3L);
        (await read.ExecuteScalarAsync<string?>("SELECT body FROM club.doc WHERE id = 1")).ShouldBeNull();
    }

    [Fact]
    public void AffectedRowCountsAreValidated()
    {
        OptimisticConcurrency.EnsureUpdated(1);
        Should.Throw<ConcurrencyConflictException>(() => OptimisticConcurrency.EnsureUpdated(0));
        Should.Throw<InvalidOperationException>(() => OptimisticConcurrency.EnsureUpdated(2))
            .ShouldNotBeOfType<ConcurrencyConflictException>();
    }

    private sealed class Contributor : IMigrationContributor
    {
        public PersistenceModuleKey Module => TransactionTests.Module;

        public IReadOnlyList<MigrationDescriptor> GetMigrations() =>
        [
            new(Module, 1, "schema", Encoding.UTF8.GetBytes("""
                CREATE SCHEMA club AUTHORIZATION club_owner;
                REVOKE ALL ON SCHEMA club FROM PUBLIC;
                GRANT USAGE ON SCHEMA club TO club_runtime;
                SET LOCAL ROLE club_owner;
                CREATE TABLE club.doc (id int PRIMARY KEY, body text, version bigint NOT NULL DEFAULT 0);
                GRANT SELECT, INSERT, UPDATE, DELETE ON club.doc TO club_runtime;
                """)),
        ];
    }
}
