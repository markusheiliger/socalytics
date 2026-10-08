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

namespace SocAlytics.Platform.Integration.Tests.Concurrency;

public sealed class OptimisticConcurrencyTests(PostgresContainerFixture postgres)
{
    private sealed class Fixture(IsolatedDatabase database, ServiceProvider provider) : IAsyncDisposable
    {
        public IsolatedDatabase Database { get; } = database;

        public AsyncServiceScope NewScope() => provider.CreateAsyncScope();

        public async ValueTask DisposeAsync()
        {
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
        return new Fixture(db, services.BuildServiceProvider());
    }

    private static async Task<Guid> SeedWidgetAsync(Fixture fixture, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        await using var connection = new NpgsqlConnection(fixture.Database.AppConnectionString);
        await connection.OpenAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO socalytics.test_widget (id, name) VALUES (@id, 'a')", new { id }, cancellationToken: ct));
        return id;
    }

    private static async Task<(string Name, long Version)> ReadAsync(Fixture fixture, Guid id, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(fixture.Database.AppConnectionString);
        await connection.OpenAsync(ct);
        return await connection.QuerySingleAsync<(string, long)>(new CommandDefinition(
            "SELECT name, version FROM socalytics.test_widget WHERE id = @id", new { id }, cancellationToken: ct));
    }

    private static async Task<VersionedWriteResult> RenameAsync(
        IDbSession session, Guid id, long expectedVersion, string name, CancellationToken ct)
    {
        var transaction = session.RequireTransaction();
        return await VersionedWrites.ExecuteAsync(
            session,
            new CommandDefinition(
                "UPDATE socalytics.test_widget SET name = @name WHERE id = @id AND version = @expectedVersion RETURNING version",
                new { id, expectedVersion, name },
                transaction,
                cancellationToken: ct),
            new CommandDefinition(
                "SELECT version FROM socalytics.test_widget WHERE id = @id",
                new { id },
                transaction,
                cancellationToken: ct));
    }

    [Fact]
    public async Task CurrentVersionIsAppliedAndCommitsAtomically()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await CreateAsync(ct);
        var id = await SeedWidgetAsync(fixture, ct);

        await using (var scope = fixture.NewScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDbSession>();
            await using var uow = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().BeginAsync(ct);
            var result = await RenameAsync(session, id, 1, "b", ct);
            result.ShouldBe(new VersionedWriteResult(VersionedWriteOutcome.Applied, 2));
            await uow.CommitAsync(ct);
        }

        (await ReadAsync(fixture, id, ct)).ShouldBe(("b", 2L));
    }

    [Fact]
    public async Task GuardedWriteThatChangesNothingKeepsVersion()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await CreateAsync(ct);
        var id = await SeedWidgetAsync(fixture, ct);

        await using var scope = fixture.NewScope();
        var session = scope.ServiceProvider.GetRequiredService<IDbSession>();
        await using var uow = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().BeginAsync(ct);
        var result = await RenameAsync(session, id, 1, "a", ct);
        result.ShouldBe(new VersionedWriteResult(VersionedWriteOutcome.Applied, 1));
        await uow.CommitAsync(ct);

        (await ReadAsync(fixture, id, ct)).ShouldBe(("a", 1L));
    }

    [Fact]
    public async Task StaleVersionConflictsAndNothingIsCommitted()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await CreateAsync(ct);
        var id = await SeedWidgetAsync(fixture, ct);
        var otherId = await SeedWidgetAsync(fixture, ct);

        await using (var scope = fixture.NewScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDbSession>();
            await using var uow = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().BeginAsync(ct);
            (await RenameAsync(session, otherId, 1, "earlier", ct)).Outcome.ShouldBe(VersionedWriteOutcome.Applied);
            var result = await RenameAsync(session, id, 7, "contested", ct);
            result.ShouldBe(new VersionedWriteResult(VersionedWriteOutcome.ConcurrencyConflict, 1));
        }

        (await ReadAsync(fixture, id, ct)).ShouldBe(("a", 1L));
        (await ReadAsync(fixture, otherId, ct)).ShouldBe(("a", 1L));
    }

    [Fact]
    public async Task UnknownIdIsNotFound()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await CreateAsync(ct);

        await using var scope = fixture.NewScope();
        var session = scope.ServiceProvider.GetRequiredService<IDbSession>();
        await using var uow = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().BeginAsync(ct);
        var result = await RenameAsync(session, Guid.NewGuid(), 1, "x", ct);
        result.ShouldBe(new VersionedWriteResult(VersionedWriteOutcome.NotFound, null));
    }

    [Fact]
    public async Task TwoWritersReadingSameVersionYieldOneAppliedAndOneConflict()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await CreateAsync(ct);
        var id = await SeedWidgetAsync(fixture, ct);

        await using var first = fixture.NewScope();
        await using var second = fixture.NewScope();

        async Task<VersionedWriteResult> WriteAsync(AsyncServiceScope scope, string name)
        {
            var session = scope.ServiceProvider.GetRequiredService<IDbSession>();
            await using var uow = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().BeginAsync(ct);
            var result = await RenameAsync(session, id, 1, name, ct);
            if (result.Outcome == VersionedWriteOutcome.Applied)
            {
                await uow.CommitAsync(ct);
            }

            return result;
        }

        var results = new[] { await WriteAsync(first, "first"), await WriteAsync(second, "second") };

        results.Count(r => r.Outcome == VersionedWriteOutcome.Applied).ShouldBe(1);
        results.Single(r => r.Outcome == VersionedWriteOutcome.ConcurrencyConflict).Version.ShouldBe(2);
        (await ReadAsync(fixture, id, ct)).ShouldBe(("first", 2L));
    }

    [Fact]
    public async Task WriterHoldingVersionFromBeforePartAdditionConflicts()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await CreateAsync(ct);
        var id = await SeedWidgetAsync(fixture, ct);

        await using (var partScope = fixture.NewScope())
        {
            var session = partScope.ServiceProvider.GetRequiredService<IDbSession>();
            await using var uow = await partScope.ServiceProvider.GetRequiredService<IUnitOfWork>().BeginAsync(ct);
            var connection = await session.GetConnectionAsync(ct);
            await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO socalytics.test_widget_part (id, widget_id, label) VALUES (@partId, @id, 'p')",
                new { partId = Guid.NewGuid(), id },
                session.Transaction,
                cancellationToken: ct));
            await uow.CommitAsync(ct);
        }

        await using var scope = fixture.NewScope();
        var staleSession = scope.ServiceProvider.GetRequiredService<IDbSession>();
        await using var staleUow = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().BeginAsync(ct);
        var result = await RenameAsync(staleSession, id, 1, "stale", ct);
        result.ShouldBe(new VersionedWriteResult(VersionedWriteOutcome.ConcurrencyConflict, 2));
    }

    [Fact]
    public async Task UnguardedChangeAdvancesVersionByOne()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await CreateAsync(ct);
        var id = await SeedWidgetAsync(fixture, ct);

        await using (var scope = fixture.NewScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDbSession>();
            await using var uow = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().BeginAsync(ct);
            var connection = await session.GetConnectionAsync(ct);
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE socalytics.test_widget SET name = 'c' WHERE id = @id",
                new { id },
                session.Transaction,
                cancellationToken: ct));
            await uow.CommitAsync(ct);
        }

        (await ReadAsync(fixture, id, ct)).ShouldBe(("c", 2L));
    }
}
