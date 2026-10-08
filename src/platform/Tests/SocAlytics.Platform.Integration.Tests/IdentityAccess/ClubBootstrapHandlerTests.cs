using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using SocAlytics.Platform.Application.Club;
using SocAlytics.Platform.Integration.Tests.IdentityAccess.Support;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Migrator;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.IdentityAccess;

public sealed class ClubBootstrapHandlerTests(PostgresContainerFixture postgres)
{
    private const string Password = "initial-password-1";

    private static readonly BootstrapClubCommand Configured = new("Test Club", "club-admin", Password);

    private async Task<IsolatedDatabase> MigratedAsync(CancellationToken ct)
    {
        var db = await postgres.CreateDatabaseAsync(ct);
        (await MigratorHarness.RunAsync(db, TestMigrationCatalogs.Platform(), ct)).ExitCode
            .ShouldBe(MigratorExitCode.Success);
        return db;
    }

    private static async Task<BootstrapOutcome> RunAsync(ServiceProvider provider, BootstrapClubCommand command, CancellationToken ct)
    {
        await using var scope = provider.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<BootstrapClubHandler>().HandleAsync(command, ct);
        return result.Value;
    }

    private static async Task<long> CountAsync(IsolatedDatabase db, string table, CancellationToken ct, string? where = null)
    {
        await using var c = new NpgsqlConnection(db.AppConnectionString);
        await c.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand($"SELECT count(*) FROM socalytics.{table} {where}", c);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    [Fact]
    public async Task FreshDatabaseCreatesClubAdminAndOneAuditEventWithoutPassword()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await MigratedAsync(ct);
        await using var provider = PlatformServices.Build(db);

        (await RunAsync(provider, Configured, ct)).ShouldBe(BootstrapOutcome.Created);

        (await CountAsync(db, "club", ct)).ShouldBe(1);
        (await CountAsync(db, "member_account", ct, "WHERE membership_status = 'active' AND password_change_required")).ShouldBe(1);
        (await CountAsync(db, "club_role_assignment", ct, "WHERE role = 'club-admin' AND assigned_by_account_id IS NULL")).ShouldBe(1);
        (await CountAsync(db, "security_audit_event", ct, "WHERE event_type = 'club.bootstrapped' AND actor_kind = 'system'")).ShouldBe(1);
        (await CountAsync(db, "security_audit_event", ct, $"WHERE details::text LIKE '%{Password}%'")).ShouldBe(0);
    }

    [Fact]
    public async Task SecondRunChangesNothingAndRecordsNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await MigratedAsync(ct);
        await using var provider = PlatformServices.Build(db);
        await RunAsync(provider, Configured, ct);

        (await RunAsync(provider, Configured, ct)).ShouldBe(BootstrapOutcome.AlreadyEstablished);

        (await CountAsync(db, "club", ct)).ShouldBe(1);
        (await CountAsync(db, "member_account", ct)).ShouldBe(1);
        (await CountAsync(db, "security_audit_event", ct)).ShouldBe(1);
    }

    [Fact]
    public async Task DifferentAccountNameIsRefusedAndAudited()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await MigratedAsync(ct);
        await using var provider = PlatformServices.Build(db);
        await RunAsync(provider, Configured, ct);

        (await RunAsync(provider, Configured with { FirstClubAdminAccountName = "someone-else" }, ct)).ShouldBe(BootstrapOutcome.Conflict);

        (await CountAsync(db, "member_account", ct)).ShouldBe(1);
        (await CountAsync(db, "security_audit_event", ct, "WHERE event_type = 'club.bootstrap-refused' AND outcome = 'refused' AND reason_code = 'bootstrap-conflict'")).ShouldBe(1);
        (await CountAsync(db, "security_audit_event", ct)).ShouldBe(2);
    }

    [Fact]
    public async Task MissingConfigurationCreatesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await MigratedAsync(ct);
        await using var provider = PlatformServices.Build(db);

        (await RunAsync(provider, new BootstrapClubCommand(null, null, null), ct)).ShouldBe(BootstrapOutcome.NotConfigured);

        (await CountAsync(db, "club", ct)).ShouldBe(0);
        (await CountAsync(db, "member_account", ct)).ShouldBe(0);
        (await CountAsync(db, "security_audit_event", ct)).ShouldBe(0);
    }

    [Fact]
    public async Task ExistingClubWithoutInitialPasswordSucceedsWithoutChange()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await MigratedAsync(ct);
        await using var provider = PlatformServices.Build(db);
        await RunAsync(provider, Configured, ct);

        (await RunAsync(provider, Configured with { FirstClubAdminInitialPassword = null }, ct)).ShouldBe(BootstrapOutcome.AlreadyEstablished);
        (await RunAsync(provider, new BootstrapClubCommand(null, null, null), ct)).ShouldBe(BootstrapOutcome.AlreadyEstablished);

        (await CountAsync(db, "security_audit_event", ct)).ShouldBe(1);
    }

    [Fact]
    public async Task ConcurrentHandlersCreateExactlyOneClub()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await MigratedAsync(ct);
        await using var provider = PlatformServices.Build(db);

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => RunAsync(provider, Configured, ct)));

        outcomes.Count(o => o == BootstrapOutcome.Created).ShouldBe(1);
        outcomes.Count(o => o == BootstrapOutcome.AlreadyEstablished).ShouldBe(3);
        (await CountAsync(db, "club", ct)).ShouldBe(1);
        (await CountAsync(db, "member_account", ct)).ShouldBe(1);
        (await CountAsync(db, "security_audit_event", ct, "WHERE event_type = 'club.bootstrapped'")).ShouldBe(1);
    }
}
