using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Infrastructure.IdentityAccess;
using SocAlytics.Platform.Integration.Tests.IdentityAccess.Support;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Migrator;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.IdentityAccess;

public sealed class IdentityStoreTests(PostgresContainerFixture postgres)
{
    private const string Password = "correct-horse-1";

    private async Task<IsolatedDatabase> MigratedAsync(CancellationToken ct)
    {
        var db = await postgres.CreateDatabaseAsync(ct);
        (await MigratorHarness.RunAsync(db, TestMigrationCatalogs.Platform(), ct)).ExitCode
            .ShouldBe(MigratorExitCode.Success);
        return db;
    }

    [Fact]
    public async Task CreateStoresPbkdf2HashAndSecurityStamp()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await MigratedAsync(ct);
        await using var provider = PlatformServices.Build(db);
        await using var scope = provider.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityMemberAccount>>();
        await using var uow = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().BeginAsync(ct);

        var account = new IdentityMemberAccount { AccountName = "Coach.One" };
        (await users.CreateAsync(account, Password)).Succeeded.ShouldBeTrue();

        var found = await users.FindByNameAsync("coach.one");
        found.ShouldNotBeNull();
        found.Id.ShouldBe(account.Id);
        found.PasswordHash.ShouldNotBeNullOrEmpty();
        found.PasswordHash.ShouldNotContain(Password);
        Convert.FromBase64String(found.PasswordHash)[0].ShouldBe((byte)0x01);
        found.SecurityStamp.ShouldNotBeNullOrEmpty();
        found.MembershipStatus.ShouldBe("active");
        await uow.CommitAsync(ct);
    }

    [Fact]
    public async Task ShortPasswordFailsValidation()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await MigratedAsync(ct);
        await using var provider = PlatformServices.Build(db);
        await using var scope = provider.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityMemberAccount>>();
        await using var uow = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().BeginAsync(ct);

        var result = await users.CreateAsync(new IdentityMemberAccount { AccountName = "short" }, "elevenchars");
        result.Succeeded.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Code == "PasswordTooShort");
    }

    [Fact]
    public async Task LockoutAndSecurityStampUpdateWork()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await MigratedAsync(ct);
        await using var provider = PlatformServices.Build(db);
        await using var scope = provider.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityMemberAccount>>();
        await using var uow = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().BeginAsync(ct);

        var account = new IdentityMemberAccount { AccountName = "locked" };
        (await users.CreateAsync(account, Password)).Succeeded.ShouldBeTrue();

        for (var i = 0; i < 5; i++)
        {
            (await users.AccessFailedAsync(account)).Succeeded.ShouldBeTrue();
        }

        (await users.IsLockedOutAsync(account)).ShouldBeTrue();

        var before = account.SecurityStamp;
        (await users.UpdateSecurityStampAsync(account)).Succeeded.ShouldBeTrue();
        (await users.FindByNameAsync("locked"))!.SecurityStamp.ShouldNotBe(before);
    }

    [Fact]
    public async Task WriteOutsideUnitOfWorkThrows()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await MigratedAsync(ct);
        await using var provider = PlatformServices.Build(db);
        await using var scope = provider.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityMemberAccount>>();

        await Should.ThrowAsync<InvalidOperationException>(
            () => users.CreateAsync(new IdentityMemberAccount { AccountName = "nouow" }, Password));
    }

    [Fact]
    public void CreateTokenYieldsUrlSafeDistinctValues()
    {
        var first = SecretHashing.CreateToken();
        var second = SecretHashing.CreateToken();

        first.Length.ShouldBe(43);
        first.ShouldMatch("^[A-Za-z0-9_-]{43}$");
        first.ShouldNotBe(second);
        SecretHashing.Sha256(first).Length.ShouldBe(32);
        SecretHashing.FixedTimeEquals(SecretHashing.Sha256(first), SecretHashing.Sha256(first)).ShouldBeTrue();
    }
}
