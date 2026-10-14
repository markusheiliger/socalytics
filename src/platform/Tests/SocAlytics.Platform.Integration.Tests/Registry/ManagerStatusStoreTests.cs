using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using SocAlytics.Platform.Application;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.Registry.Abstractions;
using SocAlytics.Platform.Application.Registry.StatusReporting;
using SocAlytics.Platform.Domain.Registry;
using SocAlytics.Platform.Infrastructure;
using SocAlytics.Platform.Infrastructure.Persistence;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Migrator;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Registry;

public sealed class ManagerStatusStoreTests(PostgresContainerFixture postgres)
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static async Task<(IsolatedDatabase Db, ServiceProvider Provider)> CreateAsync(PostgresContainerFixture postgres, CancellationToken ct)
    {
        var db = await postgres.CreateDatabaseAsync(ct);
        (await MigratorHarness.RunAsync(db, TestMigrationCatalogs.Platform(), ct)).ExitCode.ShouldBe(MigratorExitCode.Success);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"ConnectionStrings:{DatabaseConnectionNames.Runtime}"] = db.AppConnectionString,
        }).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddTestHostEnvironment();
        services.AddInfrastructure();
        services.AddApplication();
        return (db, services.BuildServiceProvider());
    }

    private static async Task<Guid> InsertRegistrationAsync(IServiceScope scope, int seed, CancellationToken ct)
    {
        var x = new string((char)('A' + seed), 43);
        var jwk = $"{{\"kty\":\"EC\",\"crv\":\"P-256\",\"x\":\"{x}\",\"y\":\"{x}\"}}";
        DeviceKeyThumbprint.TryCreate(JsonDocument.Parse(jwk).RootElement, out var thumbprint).ShouldBeTrue();
        DeviceMetadata.TryCreate("linux", "6.1", "x64", "1.0.0", "software", "host", out var metadata, out _).ShouldBeTrue();
        var r = AnalystManagerRegistration.Start(
            Guid.CreateVersion7(), "stamp", thumbprint!, jwk, "nonce-0123456789abcdef", metadata!, "10.0.0.1",
            "corr-" + seed, Now, RegistrationWindows.Default);
        await using var uow = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().BeginAsync(ct);
        await scope.ServiceProvider.GetRequiredService<IRegistrationStore>()
            .InsertAsync(r, null, Enumerable.Repeat((byte)seed, 32).ToArray(), ct);
        await uow.CommitAsync(ct);
        return r.Id;
    }

    private static ManagerStatusReport Report(string state, DateTimeOffset? pausedUntil = null, string? reason = null, int active = 1, int max = 2)
    {
        ManagerStatusReport.TryCreate(state, pausedUntil, reason, active, max, "0.3.0", out var report, out _).ShouldBeTrue();
        return report!;
    }

    private static async Task InUnitOfWorkAsync(IServiceScope scope, Func<Task> action, CancellationToken ct)
    {
        await using var uow = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().BeginAsync(ct);
        await action();
        await uow.CommitAsync(ct);
    }

    [Fact]
    public async Task UpsertRoundTripsEveryColumnAndOverwritesWithoutHistory()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, provider) = await CreateAsync(postgres, ct);
        await using var _ = db;
        await using var __ = provider;
        await using var scope = provider.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IManagerStatusStore>();
        var id = await InsertRegistrationAsync(scope, 1, ct);
        var pause = new DateTimeOffset(2026, 10, 12, 18, 0, 0, TimeSpan.Zero);

        (await store.FindAsync(id, ct)).ShouldBeNull();

        await InUnitOfWorkAsync(scope, () => store.UpsertAsync(id, Report("paused", pause), Now, 60, ct), ct);
        var first = (await store.FindAsync(id, ct))!;
        first.Report.OperatingState.ShouldBe(ManagerOperatingState.Paused);
        first.Report.PausedUntil.ShouldBe(pause);
        first.Report.RuntimeUnavailableReason.ShouldBeNull();
        first.Report.ActiveAnalysts.ShouldBe(1);
        first.Report.MaxAnalysts.ShouldBe(2);
        first.Report.ManagerVersion.ShouldBe("0.3.0");
        first.ReceivedAt.ShouldBe(Now);
        first.IntervalSeconds.ShouldBe(60);

        await InUnitOfWorkAsync(scope, () => store.UpsertAsync(id, Report("runtime-unavailable", null, "runtime-missing", 0, 3), Now.AddMinutes(1), 90, ct), ct);
        var second = (await store.FindAsync(id, ct))!;
        second.Report.OperatingState.ShouldBe(ManagerOperatingState.RuntimeUnavailable);
        second.Report.PausedUntil.ShouldBeNull();
        second.Report.RuntimeUnavailableReason.ShouldBe("runtime-missing");
        second.ReceivedAt.ShouldBe(Now.AddMinutes(1));
        second.IntervalSeconds.ShouldBe(90);

        await using var connection = new NpgsqlConnection(db.AppConnectionString);
        await connection.OpenAsync(ct);
        await using var count = new NpgsqlCommand("SELECT count(*) FROM socalytics.analyst_manager_status", connection);
        ((long)(await count.ExecuteScalarAsync(ct))!).ShouldBe(1);
    }

    [Fact]
    public async Task FindManyReturnsOnlyReportedRegistrationsAndDeleteRemovesOne()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, provider) = await CreateAsync(postgres, ct);
        await using var _ = db;
        await using var __ = provider;
        await using var scope = provider.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IManagerStatusStore>();
        var a = await InsertRegistrationAsync(scope, 1, ct);
        var b = await InsertRegistrationAsync(scope, 2, ct);
        var c = await InsertRegistrationAsync(scope, 3, ct);
        await InUnitOfWorkAsync(scope, async () =>
        {
            await store.UpsertAsync(a, Report("running"), Now, 60, ct);
            await store.UpsertAsync(b, Report("draining"), Now, 60, ct);
        }, ct);

        var found = await store.FindManyAsync([a, b, c], ct);
        found.Keys.OrderBy(k => k).ShouldBe(new[] { a, b }.OrderBy(k => k));
        found[b].Report.OperatingState.ShouldBe(ManagerOperatingState.Draining);
        (await store.FindManyAsync([], ct)).ShouldBeEmpty();

        int deleted = 0;
        await InUnitOfWorkAsync(scope, async () => deleted = await store.DeleteAsync(a, ct), ct);
        deleted.ShouldBe(1);
        (await store.FindAsync(a, ct)).ShouldBeNull();
        (await store.FindAsync(b, ct)).ShouldNotBeNull();
        await InUnitOfWorkAsync(scope, async () => deleted = await store.DeleteAsync(a, ct), ct);
        deleted.ShouldBe(0);
    }

    [Fact]
    public async Task DatabaseRefusesInvalidRows()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, provider) = await CreateAsync(postgres, ct);
        await using var _ = db;
        await using var __ = provider;
        await using var scope = provider.CreateAsyncScope();
        var id = await InsertRegistrationAsync(scope, 1, ct);
        await using var connection = new NpgsqlConnection(db.AppConnectionString);
        await connection.OpenAsync(ct);

        async Task<string?> Insert(string state, string paused, int active, int max)
        {
            await using var command = new NpgsqlCommand(
                "INSERT INTO socalytics.analyst_manager_status VALUES (@id, @state, " + paused + ", NULL, @active, @max, 'v', now(), 60)", connection);
            command.Parameters.AddWithValue("id", id);
            command.Parameters.AddWithValue("state", state);
            command.Parameters.AddWithValue("active", active);
            command.Parameters.AddWithValue("max", max);
            return (await Should.ThrowAsync<PostgresException>(() => command.ExecuteNonQueryAsync(ct))).ConstraintName;
        }

        (await Insert("sleeping", "NULL", 0, 1)).ShouldBe("ck_analyst_manager_status_state");
        (await Insert("running", "now()", 0, 1)).ShouldBe("ck_analyst_manager_status_paused_until");
        (await Insert("running", "NULL", 3, 2)).ShouldBe("ck_analyst_manager_status_analysts");
    }
}
