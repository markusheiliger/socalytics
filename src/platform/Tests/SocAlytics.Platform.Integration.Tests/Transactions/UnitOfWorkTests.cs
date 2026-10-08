using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Infrastructure;
using SocAlytics.Platform.Infrastructure.Persistence;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Migrator;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Transactions;

public sealed class UnitOfWorkTests(PostgresContainerFixture postgres)
{
    private sealed class Fixture(IsolatedDatabase database, ServiceProvider provider, AsyncServiceScope scope) : IAsyncDisposable
    {
        public IsolatedDatabase Database { get; } = database;

        public IUnitOfWork UnitOfWork => scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        public IDbSession Session => scope.ServiceProvider.GetRequiredService<IDbSession>();

        public async ValueTask DisposeAsync()
        {
            await scope.DisposeAsync();
            await provider.DisposeAsync();
            await Database.DisposeAsync();
        }
    }

    private async Task<Fixture> CreateAsync(CancellationToken ct)
    {
        var db = await postgres.CreateDatabaseAsync(ct);
        var result = await MigratorHarness.RunAsync(db, TestMigrationCatalogs.With("Versioning"), ct);
        result.ExitCode.ShouldBe(MigratorExitCode.Success);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"ConnectionStrings:{DatabaseConnectionNames.Runtime}"] = db.AppConnectionString,
            })
            .Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddInfrastructure();
        var provider = services.BuildServiceProvider();
        return new Fixture(db, provider, provider.CreateAsyncScope());
    }

    private static async Task InsertAsync(IDbSession session, Guid id, CancellationToken ct)
    {
        var connection = await session.GetConnectionAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO socalytics.test_widget (id, name) VALUES (@id, 'a')",
            new { id },
            session.Transaction,
            cancellationToken: ct));
    }

    private static async Task<long> CountAsync(string connectionString, Guid id, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        return await connection.ExecuteScalarAsync<long>(
            new CommandDefinition("SELECT count(*) FROM socalytics.test_widget WHERE id = @id", new { id }, cancellationToken: ct));
    }

    private static async Task AssertNoIdleInTransactionAsync(Fixture fixture, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(fixture.Database.SuperuserConnectionString);
        await connection.OpenAsync(ct);
        var count = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT count(*) FROM pg_stat_activity WHERE datname = @name AND state LIKE 'idle in transaction%'",
            new { name = fixture.Database.Name },
            cancellationToken: ct));
        count.ShouldBe(0);
    }

    private static async Task AssertReusableAsync(Fixture fixture, CancellationToken ct)
    {
        await using var scope = await fixture.UnitOfWork.BeginAsync(ct);
        fixture.Session.RequireTransaction().ShouldNotBeNull();
        await scope.RollbackAsync(ct);
    }

    [Fact]
    public async Task CommittedChangesAreVisibleFromAnotherConnection()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await CreateAsync(ct);
        var id = Guid.NewGuid();

        await using (var scope = await fixture.UnitOfWork.BeginAsync(ct))
        {
            await InsertAsync(fixture.Session, id, ct);
            (await CountAsync(fixture.Database.AppConnectionString, id, ct)).ShouldBe(0);
            await scope.CommitAsync(ct);
        }

        (await CountAsync(fixture.Database.AppConnectionString, id, ct)).ShouldBe(1);
        await AssertReusableAsync(fixture, ct);
        await AssertNoIdleInTransactionAsync(fixture, ct);
    }

    [Fact]
    public async Task ExceptionBeforeCommitLeavesNothingCommitted()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await CreateAsync(ct);
        var id = Guid.NewGuid();

        await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            await using var scope = await fixture.UnitOfWork.BeginAsync(ct);
            await InsertAsync(fixture.Session, id, ct);
            throw new InvalidOperationException("boom");
        });

        (await CountAsync(fixture.Database.AppConnectionString, id, ct)).ShouldBe(0);
        await AssertReusableAsync(fixture, ct);
        await AssertNoIdleInTransactionAsync(fixture, ct);
    }

    [Fact]
    public async Task DisposeWithoutCommitLeavesNothingCommitted()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await CreateAsync(ct);
        var id = Guid.NewGuid();

        await using (await fixture.UnitOfWork.BeginAsync(ct))
        {
            await InsertAsync(fixture.Session, id, ct);
        }

        (await CountAsync(fixture.Database.AppConnectionString, id, ct)).ShouldBe(0);
        await AssertReusableAsync(fixture, ct);
        await AssertNoIdleInTransactionAsync(fixture, ct);
    }

    [Fact]
    public async Task ExplicitRollbackLeavesNothingCommitted()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await CreateAsync(ct);
        var id = Guid.NewGuid();

        await using (var scope = await fixture.UnitOfWork.BeginAsync(ct))
        {
            await InsertAsync(fixture.Session, id, ct);
            await scope.RollbackAsync(ct);
            await Should.ThrowAsync<InvalidOperationException>(() => scope.CommitAsync(ct));
        }

        (await CountAsync(fixture.Database.AppConnectionString, id, ct)).ShouldBe(0);
        await AssertReusableAsync(fixture, ct);
        await AssertNoIdleInTransactionAsync(fixture, ct);
    }

    [Fact]
    public async Task CancelledCommitLeavesNothingCommitted()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await CreateAsync(ct);
        var id = Guid.NewGuid();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await using (var scope = await fixture.UnitOfWork.BeginAsync(ct))
        {
            await InsertAsync(fixture.Session, id, ct);
            await Should.ThrowAsync<OperationCanceledException>(() => scope.CommitAsync(cancelled.Token));
        }

        (await CountAsync(fixture.Database.AppConnectionString, id, ct)).ShouldBe(0);
        await AssertReusableAsync(fixture, ct);
        await AssertNoIdleInTransactionAsync(fixture, ct);
    }

    [Fact]
    public async Task SecondBeginWhileActiveThrows()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await CreateAsync(ct);

        await using var scope = await fixture.UnitOfWork.BeginAsync(ct);
        await Should.ThrowAsync<InvalidOperationException>(() => fixture.UnitOfWork.BeginAsync(ct));
    }

    [Fact]
    public async Task RequireTransactionOutsideScopeThrows()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await CreateAsync(ct);

        fixture.Session.Transaction.ShouldBeNull();
        Should.Throw<InvalidOperationException>(() => fixture.Session.RequireTransaction());
    }

    [Fact]
    public async Task CommitAfterCommitThrows()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await CreateAsync(ct);

        await using var scope = await fixture.UnitOfWork.BeginAsync(ct);
        await scope.CommitAsync(ct);
        await Should.ThrowAsync<InvalidOperationException>(() => scope.CommitAsync(ct));
        await Should.ThrowAsync<InvalidOperationException>(() => scope.RollbackAsync(ct));
    }
}
