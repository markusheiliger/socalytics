using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using SocAlytics.Platform.Application;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.Registry.Abstractions;
using SocAlytics.Platform.Domain.Registry;
using SocAlytics.Platform.Infrastructure;
using SocAlytics.Platform.Infrastructure.Persistence;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Migrator;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Registry;

public sealed class RegistrationStoreTests(PostgresContainerFixture postgres)
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private async Task<(IsolatedDatabase Db, ServiceProvider Provider)> CreateAsync(CancellationToken ct)
    {
        var db = await postgres.CreateDatabaseAsync(ct);
        (await MigratorHarness.RunAsync(db, TestMigrationCatalogs.Platform(), ct)).ExitCode.ShouldBe(MigratorExitCode.Success);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"ConnectionStrings:{DatabaseConnectionNames.Runtime}"] = db.AppConnectionString,
        }).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddInfrastructure();
        services.AddApplication();
        return (db, services.BuildServiceProvider());
    }

    private static AnalystManagerRegistration NewRegistration(int seed, DateTimeOffset created)
    {
        var x = new string((char)('A' + seed), 43);
        var jwk = $"{{\"kty\":\"EC\",\"crv\":\"P-256\",\"x\":\"{x}\",\"y\":\"{x}\"}}";
        DeviceKeyThumbprint.TryCreate(JsonDocument.Parse(jwk).RootElement, out var thumbprint).ShouldBeTrue();
        DeviceMetadata.TryCreate("linux", "6.1", "x64", "1.0.0", "software", "host", out var metadata, out _).ShouldBeTrue();
        return AnalystManagerRegistration.Start(
            Guid.CreateVersion7(), "stamp", thumbprint!, jwk, "nonce-0123456789abcdef", metadata!, "10.0.0.1",
            "corr-" + seed, created, RegistrationWindows.Default);
    }

    private static byte[] Bytes(byte b) => Enumerable.Repeat(b, 32).ToArray();

    [Fact]
    public async Task RoundTripsEveryColumn()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, provider) = await CreateAsync(ct);
        await using var _ = db;
        await using var __ = provider;
        await using var scope = provider.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IRegistrationStore>();
        var r = NewRegistration(1, Now);
        await using (var uow = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().BeginAsync(ct))
        {
            await store.InsertAsync(r, Bytes(1), Bytes(2), ct);
            await uow.CommitAsync(ct);
        }

        var loaded = (await store.FindByIdAsync(r.Id, ct))!;
        loaded.State.ShouldBe(RegistrationState.Pairing);
        loaded.StampId.ShouldBe("stamp");
        loaded.KeyThumbprint.ShouldBe(r.KeyThumbprint);
        loaded.DeviceFingerprint.Value.ShouldBe(r.DeviceFingerprint.Value);
        loaded.PairingNonce.ShouldBe(r.PairingNonce);
        loaded.PairingSourceAddress.ShouldBe("10.0.0.1");
        loaded.CorrelationId.ShouldBe("corr-1");
        loaded.PairingExpiresAt.ShouldBe(r.PairingExpiresAt);
        loaded.CreatedAt.ShouldBe(Now);
        loaded.DeviceMetadata.Os.ShouldBe("linux");
        loaded.DeviceMetadata.HostLabel.ShouldBe("host");
        loaded.DeviceMetadata.ClaimedKeyProtection.ShouldBe(ClaimedKeyProtection.Software);
        JsonDocument.Parse(loaded.PublicJwk).RootElement.GetProperty("crv").GetString().ShouldBe("P-256");
        (await store.FindByPairingCodeHashAsync(Bytes(1), ct))!.Id.ShouldBe(r.Id);
        (await store.FindByPollingHandleHashAsync(Bytes(2), ct))!.Id.ShouldBe(r.Id);
        (await store.FindNonTerminalByThumbprintAsync(r.KeyThumbprint, ct))!.Id.ShouldBe(r.Id);
        (await store.FindByIdAsync(Guid.NewGuid(), ct)).ShouldBeNull();
        (await store.FindByPollingHandleHashAsync(Bytes(9), ct)).ShouldBeNull();
    }

    [Fact]
    public async Task GuardedUpdateReportsOutcomesAndPersistsTransitions()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, provider) = await CreateAsync(ct);
        await using var _ = db;
        await using var __ = provider;
        await using var scope = provider.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IRegistrationStore>();
        var r = NewRegistration(2, Now);
        await using var uow = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().BeginAsync(ct);
        await store.InsertAsync(r, Bytes(3), Bytes(4), ct);

        (await store.IncrementFingerprintMismatchAsync(r.Id, RegistrationState.Pairing, ct)).Outcome.ShouldBe(VersionedWriteOutcome.Applied);
        (await store.IncrementFingerprintMismatchAsync(r.Id, RegistrationState.Approved, ct)).Outcome
            .ShouldBe(VersionedWriteOutcome.ConcurrencyConflict);
        (await store.ClearChallengeAsync(Guid.NewGuid(), RegistrationState.Pairing, ct)).Outcome.ShouldBe(VersionedWriteOutcome.NotFound);

        (await store.SetChallengeAsync(r.Id, RegistrationState.Pairing, Bytes(5), Now.AddMinutes(1), ct)).Outcome
            .ShouldBe(VersionedWriteOutcome.Applied);
        (await store.FindByIdAsync(r.Id, ct))!.ChallengeHash.ShouldBe(Bytes(5));
        (await store.ClearChallengeAsync(r.Id, RegistrationState.Pairing, ct)).Outcome.ShouldBe(VersionedWriteOutcome.Applied);
        var loaded = (await store.FindByIdAsync(r.Id, ct))!;
        loaded.ChallengeHash.ShouldBeNull();
        loaded.FingerprintMismatchCount.ShouldBe(1);

        loaded.Expire("pairing-window-elapsed", Now.AddMinutes(11)).Succeeded.ShouldBeTrue();
        (await store.UpdateAsync(loaded, RegistrationState.Approved, true, ct)).Outcome.ShouldBe(VersionedWriteOutcome.ConcurrencyConflict);
        (await store.UpdateAsync(loaded, RegistrationState.Pairing, true, ct)).Outcome.ShouldBe(VersionedWriteOutcome.Applied);
        var ended = (await store.FindByIdAsync(r.Id, ct))!;
        ended.State.ShouldBe(RegistrationState.Expired);
        ended.EndReason.ShouldBe("pairing-window-elapsed");
        (await store.FindByPairingCodeHashAsync(Bytes(3), ct)).ShouldBeNull();
        (await store.FindNonTerminalByThumbprintAsync(r.KeyThumbprint, ct)).ShouldBeNull();
    }

    [Fact]
    public async Task ListsNewestFirstWithFilterPagingAndDueForExpiry()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, provider) = await CreateAsync(ct);
        await using var _ = db;
        await using var __ = provider;
        await using var scope = provider.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IRegistrationStore>();
        var regs = Enumerable.Range(3, 3).Select(i => NewRegistration(i, Now.AddMinutes(i * 20))).ToList();
        await using (var uow = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().BeginAsync(ct))
        {
            for (var i = 0; i < regs.Count; i++)
            {
                await store.InsertAsync(regs[i], null, Bytes((byte)(10 + i)), ct);
            }

            await uow.CommitAsync(ct);
        }

        var first = await store.ListAsync(null, 2, null, ct);
        first.Items.Select(x => x.Id).ShouldBe([regs[2].Id, regs[1].Id]);
        first.ContinuationToken.ShouldNotBeNull();
        var second = await store.ListAsync(null, 2, first.ContinuationToken, ct);
        second.Items.Select(x => x.Id).ShouldBe([regs[0].Id]);
        second.ContinuationToken.ShouldBeNull();
        (await store.ListAsync(RegistrationState.Active, 10, null, ct)).Items.ShouldBeEmpty();
        (await store.ListAsync(RegistrationState.Pairing, 10, null, ct)).Items.Count.ShouldBe(3);

        var due = await store.ListDueForExpiryAsync(regs[0].PairingExpiresAt, 10, ct);
        due.Select(x => x.Id).ShouldBe([regs[0].Id]);
        (await store.ListDueForExpiryAsync(Now, 10, ct)).ShouldBeEmpty();
    }
}
