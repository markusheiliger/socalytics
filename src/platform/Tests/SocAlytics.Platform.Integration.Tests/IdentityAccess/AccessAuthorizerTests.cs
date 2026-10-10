using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.IdentityAccess;
using SocAlytics.Platform.Integration.Tests.IdentityAccess.Support;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Migrator;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.IdentityAccess;

public sealed class AccessAuthorizerTests(PostgresContainerFixture postgres)
{
    private const string Now = "now()";

    private sealed record World(Guid Team, Guid Match, Guid OtherTeam, Guid OtherMatch, Guid Admin);

    private static async Task ExecAsync(NpgsqlConnection c, string sql, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, c);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<Guid> AccountAsync(
        NpgsqlConnection c, string status = "active", bool changeRequired = false, CancellationToken ct = default)
    {
        var id = Guid.NewGuid();
        await ExecAsync(
            c,
            "INSERT INTO socalytics.member_account (id, account_name, normalized_account_name, security_stamp, " +
            $"membership_changed_at, created_at, membership_status, password_change_required) VALUES ('{id}', 'a{id:N}', 'A{id:N}', 's', {Now}, {Now}, '{status}', {changeRequired})",
            ct);
        return id;
    }

    private static Task ClubRoleAsync(NpgsqlConnection c, Guid account, string role, CancellationToken ct) =>
        ExecAsync(c, $"INSERT INTO socalytics.club_role_assignment (member_account_id, role, assigned_at) VALUES ('{account}', '{role}', {Now})", ct);

    private static Task TeamRoleAsync(NpgsqlConnection c, Guid account, Guid team, string role, Guid by, CancellationToken ct) =>
        ExecAsync(c, $"INSERT INTO socalytics.team_role_assignment (member_account_id, team_id, role, assigned_at, assigned_by_account_id) VALUES ('{account}', '{team}', '{role}', {Now}, '{by}')", ct);

    private async Task<(IsolatedDatabase Db, World World)> SeedAsync(CancellationToken ct)
    {
        var db = await postgres.CreateDatabaseAsync(ct);
        (await MigratorHarness.RunAsync(db, TestMigrationCatalogs.Platform(), ct)).ExitCode
            .ShouldBe(MigratorExitCode.Success);
        await using var c = new NpgsqlConnection(db.AppConnectionString);
        await c.OpenAsync(ct);
        var admin = await AccountAsync(c, ct: ct);
        var season = Guid.NewGuid();
        await ExecAsync(c, $"INSERT INTO socalytics.season (id, name, created_at) VALUES ('{season}', 'S', {Now})", ct);
        Guid team = Guid.NewGuid(), other = Guid.NewGuid(), match = Guid.NewGuid(), otherMatch = Guid.NewGuid();
        foreach (var (t, m) in new[] { (team, match), (other, otherMatch) })
        {
            await ExecAsync(c, $"INSERT INTO socalytics.team (id, season_id, name, created_at) VALUES ('{t}', '{season}', 'T', {Now})", ct);
            await ExecAsync(
                c,
                $"INSERT INTO socalytics.match (id, team_id, opponent_name, kickoff_at, home_away, created_at, created_by_account_id) VALUES ('{m}', '{t}', 'O', {Now}, 'home', {Now}, '{admin}')",
                ct);
        }

        return (db, new World(team, match, other, otherMatch, admin));
    }

    private static async Task<long> DenialsAsync(IsolatedDatabase db, CancellationToken ct, string? where = null)
    {
        await using var c = new NpgsqlConnection(db.AppConnectionString);
        await c.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT count(*) FROM socalytics.security_audit_event WHERE event_type = 'authorization.denied' AND outcome = 'denied'" +
            (where is null ? string.Empty : " AND " + where), c);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    private static IAccessAuthorizer Authorizer(AsyncServiceScope scope, Guid account)
    {
        var context = scope.ServiceProvider.GetRequiredService<TestRequestContext>();
        context.ActorKind = AuditActorKind.Member;
        context.MemberAccountId = account;
        return scope.ServiceProvider.GetRequiredService<IAccessAuthorizer>();
    }

    [Fact]
    public async Task ClubAdminIsGrantedEverything()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, w) = await SeedAsync(ct);
        await using var _ = db;
        await using (var c = new NpgsqlConnection(db.AppConnectionString))
        {
            await c.OpenAsync(ct);
            await ClubRoleAsync(c, w.Admin, "club-admin", ct);
        }

        await using var provider = PlatformServices.Build(db);
        await using var scope = provider.CreateAsyncScope();
        var auth = Authorizer(scope, w.Admin);

        (await auth.AuthorizeClubAsync(ClubPermission.Administer, ct)).IsGranted.ShouldBeTrue();
        (await auth.AuthorizeClubAsync(ClubPermission.Register, ct)).IsGranted.ShouldBeTrue();
        (await auth.AuthorizeTeamResourceAsync(new("team", w.OtherTeam), TeamPermission.Write, ct)).IsGranted.ShouldBeTrue();
        var match = await auth.AuthorizeTeamResourceAsync(new("match", w.Match), TeamPermission.Read, ct);
        match.Scope!.TeamId.ShouldBe(w.Team);
        var visible = await auth.GetVisibleTeamsAsync(ct);
        visible.AllTeams.ShouldBeTrue();
        visible.TeamIds.ShouldBe([w.Team, w.OtherTeam], ignoreOrder: true);
    }

    [Fact]
    public async Task RegistrarGetsRegisterOnly()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, w) = await SeedAsync(ct);
        await using var _ = db;
        Guid registrar;
        await using (var c = new NpgsqlConnection(db.AppConnectionString))
        {
            await c.OpenAsync(ct);
            registrar = await AccountAsync(c, ct: ct);
            await ClubRoleAsync(c, registrar, "registrar", ct);
        }

        await using var provider = PlatformServices.Build(db);
        await using var scope = provider.CreateAsyncScope();
        var auth = Authorizer(scope, registrar);

        (await auth.AuthorizeClubAsync(ClubPermission.Register, ct)).IsGranted.ShouldBeTrue();
        (await auth.AuthorizeClubAsync(ClubPermission.Administer, ct)).Kind.ShouldBe(AccessDecisionKind.Forbidden);
        (await auth.AuthorizeTeamResourceAsync(new("team", w.Team), TeamPermission.Read, ct)).Kind
            .ShouldBe(AccessDecisionKind.NotFound);
        var visible = await auth.GetVisibleTeamsAsync(ct);
        visible.AllTeams.ShouldBeFalse();
        visible.TeamIds.ShouldBeEmpty();
    }

    [Fact]
    public async Task CoachAndViewerAreScopedToTheirTeam()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, w) = await SeedAsync(ct);
        await using var _ = db;
        Guid coach, viewer;
        await using (var c = new NpgsqlConnection(db.AppConnectionString))
        {
            await c.OpenAsync(ct);
            coach = await AccountAsync(c, ct: ct);
            viewer = await AccountAsync(c, ct: ct);
            await TeamRoleAsync(c, coach, w.Team, "coach", w.Admin, ct);
            await TeamRoleAsync(c, viewer, w.Team, "viewer", w.Admin, ct);
        }

        await using var provider = PlatformServices.Build(db);
        await using (var scope = provider.CreateAsyncScope())
        {
            var auth = Authorizer(scope, coach);
            (await auth.AuthorizeTeamResourceAsync(new("team", w.Team), TeamPermission.Write, ct)).IsGranted.ShouldBeTrue();
            (await auth.AuthorizeTeamResourceAsync(new("match", w.Match), TeamPermission.Write, ct)).IsGranted.ShouldBeTrue();
            (await auth.GetVisibleTeamsAsync(ct)).TeamIds.ShouldBe([w.Team]);
            (await DenialsAsync(db, ct)).ShouldBe(0);

            (await auth.AuthorizeTeamResourceAsync(new("team", w.OtherTeam), TeamPermission.Read, ct)).Kind
                .ShouldBe(AccessDecisionKind.NotFound);
            (await DenialsAsync(db, ct, $"resource_type = 'team' AND resource_id = '{w.OtherTeam}' AND reason_code = 'not-visible' AND team_id = '{w.OtherTeam}'"))
                .ShouldBe(1);
            (await auth.AuthorizeTeamResourceAsync(new("match", w.OtherMatch), TeamPermission.Read, ct)).Kind
                .ShouldBe(AccessDecisionKind.NotFound);
            (await DenialsAsync(db, ct, $"resource_type = 'match' AND resource_id = '{w.OtherMatch}'")).ShouldBe(1);
        }

        await using (var scope = provider.CreateAsyncScope())
        {
            var auth = Authorizer(scope, viewer);
            (await auth.AuthorizeTeamResourceAsync(new("match", w.Match), TeamPermission.Read, ct)).IsGranted.ShouldBeTrue();
            (await auth.AuthorizeTeamResourceAsync(new("match", w.Match), TeamPermission.Write, ct)).Kind
                .ShouldBe(AccessDecisionKind.Forbidden);
            (await DenialsAsync(db, ct, "reason_code = 'insufficient-role'")).ShouldBe(1);
        }
    }

    [Fact]
    public async Task UnknownKindAndIdYieldNotFound()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, w) = await SeedAsync(ct);
        await using var _ = db;
        await using (var c = new NpgsqlConnection(db.AppConnectionString))
        {
            await c.OpenAsync(ct);
            await ClubRoleAsync(c, w.Admin, "club-admin", ct);
        }

        await using var provider = PlatformServices.Build(db);
        await using var scope = provider.CreateAsyncScope();
        var auth = Authorizer(scope, w.Admin);

        (await auth.AuthorizeTeamResourceAsync(new("widget", w.Team), TeamPermission.Read, ct)).Kind
            .ShouldBe(AccessDecisionKind.NotFound);
        (await auth.AuthorizeTeamResourceAsync(new("team", Guid.NewGuid()), TeamPermission.Read, ct)).Kind
            .ShouldBe(AccessDecisionKind.NotFound);
        (await DenialsAsync(db, ct, "reason_code = 'unknown-resource'")).ShouldBe(2);
    }

    [Theory]
    [InlineData("deactivated", false, "inactive-membership")]
    [InlineData("active", true, "password-change-required")]
    public async Task InactiveOrPasswordChangeAccountsAreDeniedEverything(string status, bool change, string reason)
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, w) = await SeedAsync(ct);
        await using var _ = db;
        Guid account;
        await using (var c = new NpgsqlConnection(db.AppConnectionString))
        {
            await c.OpenAsync(ct);
            account = await AccountAsync(c, status, change, ct);
            await ClubRoleAsync(c, account, "club-admin", ct);
        }

        await using var provider = PlatformServices.Build(db);
        await using var scope = provider.CreateAsyncScope();
        var auth = Authorizer(scope, account);

        (await auth.AuthorizeClubAsync(ClubPermission.Administer, ct)).IsGranted.ShouldBeFalse();
        (await auth.AuthorizeTeamResourceAsync(new("team", w.Team), TeamPermission.Read, ct)).Kind
            .ShouldBe(AccessDecisionKind.NotFound);
        (await auth.GetVisibleTeamsAsync(ct)).TeamIds.ShouldBeEmpty();
        (await DenialsAsync(db, ct, $"reason_code = '{reason}'")).ShouldBe(2);
    }

    [Fact]
    public async Task ClubDenialThroughResourceOverloadRecordsTheResource()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, w) = await SeedAsync(ct);
        await using var _ = db;
        await using var provider = PlatformServices.Build(db);
        await using var scope = provider.CreateAsyncScope();
        var auth = Authorizer(scope, w.Admin);
        var target = Guid.NewGuid().ToString();

        (await auth.AuthorizeClubAsync(ClubPermission.Administer, new AuditResource("member", target), ct)).Kind
            .ShouldBe(AccessDecisionKind.Forbidden);
        (await DenialsAsync(db, ct, $"resource_type = 'member' AND resource_id = '{target}' AND reason_code = 'insufficient-role'"))
            .ShouldBe(1);

        (await auth.AuthorizeClubAsync(ClubPermission.Administer, ct)).IsGranted.ShouldBeFalse();
        (await DenialsAsync(db, ct, "resource_type = 'club'")).ShouldBe(1);
    }
}
