using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using SocAlytics.Platform.Application;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.Registry.Abstractions;
using SocAlytics.Platform.Application.Registry.QueueCredentials;
using SocAlytics.Platform.Domain.Registry;
using SocAlytics.Platform.Infrastructure;
using SocAlytics.Platform.Infrastructure.Persistence;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Migrator;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Registry;

public sealed class QueueCredentialStoreTests(PostgresContainerFixture postgres)
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
        services.AddTestHostEnvironment();
        services.AddInfrastructure();
        services.AddApplication();
        return (db, services.BuildServiceProvider());
    }

    private static AnalystManagerRegistration NewRegistration(int seed)
    {
        var x = new string((char)('A' + seed), 43);
        var jwk = $"{{\"kty\":\"EC\",\"crv\":\"P-256\",\"x\":\"{x}\",\"y\":\"{x}\"}}";
        DeviceKeyThumbprint.TryCreate(JsonDocument.Parse(jwk).RootElement, out var thumbprint).ShouldBeTrue();
        DeviceMetadata.TryCreate("linux", "6.1", "x64", "1.0.0", "software", null, "host", out var metadata, out _).ShouldBeTrue();
        return AnalystManagerRegistration.Start(
            Guid.CreateVersion7(), "stamp", thumbprint!, jwk, "nonce-0123456789abcdef", metadata!, "10.0.0.1",
            "corr-" + seed, Now, RegistrationWindows.Default);
    }

    private static byte[] Bytes(byte b) => Enumerable.Repeat(b, 32).ToArray();

    private static string UserKey(char c) => "U" + new string(c, 55);

    private static IssuedQueueCredential Credential(Guid registrationId, char key, DateTimeOffset expiresAt) =>
        new(Guid.CreateVersion7(), registrationId, UserKey(key), expiresAt.AddHours(-2), expiresAt, null, null);

    private static async Task<Guid> InsertRegistrationAsync(IServiceProvider services, int seed, CancellationToken ct)
    {
        var r = NewRegistration(seed);
        await using var uow = await services.GetRequiredService<IUnitOfWork>().BeginAsync(ct);
        await services.GetRequiredService<IRegistrationStore>().InsertAsync(r, Bytes((byte)(seed * 2 + 1)), Bytes((byte)(seed * 2 + 2)), ct);
        await uow.CommitAsync(ct);
        return r.Id;
    }

    [Fact]
    public async Task RecordsIssuanceAndListsOnlyUnexpiredCredentialsOfTheRegistration()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, provider) = await CreateAsync(ct);
        await using var _ = db;
        await using var __ = provider;
        await using var scope = provider.CreateAsyncScope();
        var registrationA = await InsertRegistrationAsync(scope.ServiceProvider, 1, ct);
        var registrationB = await InsertRegistrationAsync(scope.ServiceProvider, 2, ct);
        var store = scope.ServiceProvider.GetRequiredService<IQueueCredentialStore>();
        var live = Credential(registrationA, 'A', Now.AddHours(1));
        await using (var uow = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().BeginAsync(ct))
        {
            (await store.RecordIssuanceAsync(live, ct)).ShouldBeTrue();
            (await store.RecordIssuanceAsync(Credential(registrationA, 'B', Now.AddHours(-1)), ct)).ShouldBeTrue();
            (await store.RecordIssuanceAsync(Credential(registrationB, 'C', Now.AddHours(1)), ct)).ShouldBeTrue();
            await uow.CommitAsync(ct);
        }

        var listed = await store.ListUnexpiredAsync(registrationA, Now, ct);
        var item = listed.ShouldHaveSingleItem();
        item.ShouldBe(live);
        item.RevokedAt.ShouldBeNull();
        item.RevocationPushedAt.ShouldBeNull();
        (await store.ListUnexpiredAsync(Guid.NewGuid(), Now, ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task RefusesReusedUserPublicKeyAndUnknownRegistration()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, provider) = await CreateAsync(ct);
        await using var _ = db;
        await using var __ = provider;
        await using var scope = provider.CreateAsyncScope();
        var registration = await InsertRegistrationAsync(scope.ServiceProvider, 1, ct);
        var store = scope.ServiceProvider.GetRequiredService<IQueueCredentialStore>();
        await using var uow = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().BeginAsync(ct);

        (await store.RecordIssuanceAsync(Credential(registration, 'A', Now.AddHours(1)), ct)).ShouldBeTrue();
        (await store.RecordIssuanceAsync(Credential(registration, 'A', Now.AddHours(2)), ct)).ShouldBeFalse();
        await Should.ThrowAsync<PostgresException>(() =>
            store.RecordIssuanceAsync(Credential(Guid.NewGuid(), 'B', Now.AddHours(1)), ct));
    }

    [Fact]
    public async Task RevocationMarksUnexpiredCredentialsOfOneRegistrationAndPushIsRecordedOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, provider) = await CreateAsync(ct);
        await using var _ = db;
        await using var __ = provider;
        await using var scope = provider.CreateAsyncScope();
        var registrationA = await InsertRegistrationAsync(scope.ServiceProvider, 1, ct);
        var registrationB = await InsertRegistrationAsync(scope.ServiceProvider, 2, ct);
        var store = scope.ServiceProvider.GetRequiredService<IQueueCredentialStore>();
        var first = Credential(registrationA, 'A', Now.AddHours(1));
        var second = Credential(registrationA, 'B', Now.AddHours(2));
        var other = Credential(registrationB, 'C', Now.AddHours(1));
        await using (var uow = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().BeginAsync(ct))
        {
            foreach (var credential in new[] { first, second, other, Credential(registrationA, 'D', Now.AddHours(-1)) })
            {
                await store.RecordIssuanceAsync(credential, ct);
            }

            (await store.MarkRevokedAsync(registrationA, Now, ct)).ShouldBe(2);
            (await store.MarkRevokedAsync(registrationA, Now.AddMinutes(1), ct)).ShouldBe(0);
            await uow.CommitAsync(ct);
        }

        var revoked = await store.ListRevokedUnexpiredAsync(Now, ct);
        revoked.Select(c => c.QueueCredentialId).ShouldBe([first.QueueCredentialId, second.QueueCredentialId], ignoreOrder: true);
        revoked.ShouldAllBe(c => c.RevokedAt == Now && c.RevocationPushedAt == null);
        (await store.ListRevokedUnexpiredAsync(Now.AddHours(1), ct)).Select(c => c.QueueCredentialId)
            .ShouldBe([second.QueueCredentialId]);

        await using (var uow = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().BeginAsync(ct))
        {
            (await store.MarkPushedAsync([], Now, ct)).ShouldBe(0);
            (await store.MarkPushedAsync([first.QueueCredentialId, other.QueueCredentialId], Now.AddMinutes(2), ct)).ShouldBe(1);
            (await store.MarkPushedAsync([first.QueueCredentialId], Now.AddMinutes(3), ct)).ShouldBe(0);
            await uow.CommitAsync(ct);
        }

        var afterPush = await store.ListRevokedUnexpiredAsync(Now, ct);
        afterPush.Single(c => c.QueueCredentialId == first.QueueCredentialId).RevocationPushedAt.ShouldBe(Now.AddMinutes(2));
        afterPush.Single(c => c.QueueCredentialId == second.QueueCredentialId).RevocationPushedAt.ShouldBeNull();
        (await store.ListUnexpiredAsync(registrationB, Now, ct)).ShouldHaveSingleItem().RevokedAt.ShouldBeNull();
    }
}
