using Npgsql;
using Shouldly;
using SocAlytics.Platform.Infrastructure.Persistence.MigrationHistory;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Migrations;

public sealed class MigrationLockTests(PostgresContainerFixture postgres)
{
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(800);

    private static async Task<NpgsqlConnection> OpenAsync(IsolatedDatabase db)
    {
        var connection = new NpgsqlConnection(db.MigratorConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        return connection;
    }

    [Fact]
    public async Task AcquiresOnIsolatedDatabase()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await postgres.CreateDatabaseAsync(ct);
        await using var connection = await OpenAsync(db);

        var waited = false;
        await using var result = await MigrationLock.AcquireAsync(connection, Short, () => waited = true, ct);

        result.Acquired.ShouldBeTrue();
        waited.ShouldBeFalse();
    }

    [Fact]
    public async Task SecondAcquisitionWaitsTimesOutThenSucceedsAfterRelease()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await postgres.CreateDatabaseAsync(ct);
        await using var first = await OpenAsync(db);
        await using var second = await OpenAsync(db);

        var held = await MigrationLock.AcquireAsync(first, Short, null, ct);
        held.Acquired.ShouldBeTrue();

        var waitingCalls = 0;
        var blocked = await MigrationLock.AcquireAsync(second, Short, () => waitingCalls++, ct);
        blocked.Acquired.ShouldBeFalse();
        waitingCalls.ShouldBe(1);

        await held.DisposeAsync();

        await using var acquired = await MigrationLock.AcquireAsync(second, Short, null, ct);
        acquired.Acquired.ShouldBeTrue();
    }

    [Fact]
    public async Task ClosingConnectionWithoutUnlockReleasesLock()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await postgres.CreateDatabaseAsync(ct);
        var first = await OpenAsync(db);
        await using var second = await OpenAsync(db);

        (await MigrationLock.AcquireAsync(first, Short, null, ct)).Acquired.ShouldBeTrue();
        await first.CloseAsync();
        await first.DisposeAsync();
        NpgsqlConnection.ClearAllPools();

        await using var acquired = await MigrationLock.AcquireAsync(second, TimeSpan.FromSeconds(10), null, ct);
        acquired.Acquired.ShouldBeTrue();
    }

    [Fact]
    public async Task LocksOnDifferentDatabasesDoNotBlockEachOther()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var dbA = await postgres.CreateDatabaseAsync(ct);
        await using var dbB = await postgres.CreateDatabaseAsync(ct);
        await using var a = await OpenAsync(dbA);
        await using var b = await OpenAsync(dbB);

        await using var lockA = await MigrationLock.AcquireAsync(a, Short, null, ct);
        await using var lockB = await MigrationLock.AcquireAsync(b, Short, null, ct);

        lockA.Acquired.ShouldBeTrue();
        lockB.Acquired.ShouldBeTrue();
    }

    [Fact]
    public async Task CancellationStopsTheWait()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await postgres.CreateDatabaseAsync(ct);
        await using var first = await OpenAsync(db);
        await using var second = await OpenAsync(db);

        await using var held = await MigrationLock.AcquireAsync(first, Short, null, ct);
        held.Acquired.ShouldBeTrue();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromMilliseconds(300));

        await Should.ThrowAsync<OperationCanceledException>(() =>
            MigrationLock.AcquireAsync(second, TimeSpan.FromMinutes(1), null, cts.Token));
    }
}
