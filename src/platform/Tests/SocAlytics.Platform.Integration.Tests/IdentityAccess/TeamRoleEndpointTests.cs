using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
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

public sealed class TeamRoleEndpointTests(PostgresContainerFixture postgres)
{
	private static readonly Dictionary<string, string?> Config = new()
	{
		["ClubDisplayName"] = "Role Club",
		["FirstClubAdmin:AccountName"] = "role-bootstrap",
		["FirstClubAdmin:InitialPassword"] = "Initial-Admin-Pass-1234",
	};

	private async Task<(IsolatedDatabase Db, PlatformApiFactory Factory)> StartAsync(CancellationToken ct)
	{
		var db = await postgres.CreateDatabaseAsync(ct);
		(await MigratorHarness.RunAsync(db, TestMigrationCatalogs.Platform(), ct)).ExitCode.ShouldBe(MigratorExitCode.Success);
		var factory = new PlatformApiFactory(db, Config);
		await factory.WaitUntilHealthyAsync(ct);
		return (db, factory);
	}

	private static async Task<T> ScalarAsync<T>(IsolatedDatabase db, string sql, CancellationToken ct)
	{
		await using var connection = new NpgsqlConnection(db.MigratorConnectionString);
		await connection.OpenAsync(ct);
		await using var command = new NpgsqlCommand(sql, connection);
		return (T)(await command.ExecuteScalarAsync(ct))!;
	}

	private static async Task<Guid> CreateTeamAsync(ApiSession admin, Guid season, string name, CancellationToken ct)
	{
		using var response = await admin.SendAsync(HttpMethod.Post, $"/api/v1/seasons/{season}/teams", new { name }, ct);
		response.StatusCode.ShouldBe(HttpStatusCode.Created);
		return (await response.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("id").GetGuid();
	}

	private static string RolePath(Guid member, Guid team) => $"/api/v1/members/{member}/team-roles/{team}";

	private static async Task<HttpResponseMessage> AssignAsync(ApiSession admin, Guid member, Guid team, string role, CancellationToken ct) =>
		await admin.SendAsync(HttpMethod.Put, RolePath(member, team), new { role }, ct);

	private static async Task<HashSet<Guid>> ListedTeamsAsync(ApiSession s, CancellationToken ct)
	{
		using var response = await s.SendAsync(HttpMethod.Get, "/api/v1/teams", null, ct);
		response.StatusCode.ShouldBe(HttpStatusCode.OK);
		var items = (await response.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("items");
		return [.. items.EnumerateArray().Select(i => i.GetProperty("id").GetGuid())];
	}

	[Fact]
	public async Task TeamRolesScopeVisibilityAndRevocationTakesEffectImmediately()
	{
		var ct = TestContext.Current.CancellationToken;
		var (db, factory) = await StartAsync(ct);
		await using var _ = db;
		await using var __ = factory;
		await TestMembers.SeedAsync(db, "role-admin", clubRoles: ["club-admin"], cancellationToken: ct);
		var memberId = await TestMembers.SeedAsync(db, "role-member", cancellationToken: ct);
		using var admin = await ApiSession.SignInAsync(factory, "role-admin", TestMembers.DefaultPassword, ct);
		using var member = await ApiSession.SignInAsync(factory, "role-member", TestMembers.DefaultPassword, ct);

		using var seasonResponse = await admin.SendAsync(HttpMethod.Post, "/api/v1/seasons", new { name = "2026" }, ct);
		var season = (await seasonResponse.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("id").GetGuid();
		var teamA = await CreateTeamAsync(admin, season, "A", ct);
		var teamB = await CreateTeamAsync(admin, season, "B", ct);
		var teamC = await CreateTeamAsync(admin, season, "C", ct);

		using (var assigned = await AssignAsync(admin, memberId, teamA, "coach", ct))
		{
			assigned.StatusCode.ShouldBe(HttpStatusCode.OK, await assigned.Content.ReadAsStringAsync(ct));
			assigned.Headers.ETag.ShouldNotBeNull();
		}

		using (var assigned = await AssignAsync(admin, memberId, teamB, "viewer", ct))
		{
			assigned.StatusCode.ShouldBe(HttpStatusCode.OK);
		}

		using (var again = await AssignAsync(admin, memberId, teamB, "viewer", ct))
		{
			again.StatusCode.ShouldBe(HttpStatusCode.OK);
		}

		(await ScalarAsync<long>(db, "SELECT count(*) FROM socalytics.security_audit_event WHERE event_type = 'team-role.assigned' AND outcome = 'succeeded'", ct)).ShouldBe(2);
		(await ScalarAsync<long>(db, "SELECT count(*) FROM socalytics.security_audit_event WHERE event_type = 'team-role.assigned' AND outcome = 'unchanged'", ct)).ShouldBe(1);
		(await ScalarAsync<long>(db, $"SELECT count(*) FROM socalytics.security_audit_event WHERE event_type = 'team-role.assigned' AND team_id = '{teamA}' AND details->>'role' = 'coach' AND actor_account_id IS NOT NULL", ct)).ShouldBe(1);

		(await ListedTeamsAsync(member, ct)).ShouldBe([teamA, teamB], ignoreOrder: true);
		using (var me = await member.SendAsync(HttpMethod.Get, "/api/v1/me", null, ct))
		{
			var roles = (await me.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("teamRoles");
			roles.EnumerateArray().Select(r => r.GetProperty("teamId").GetGuid()).ShouldBe([teamA, teamB], ignoreOrder: true);
		}

		using (var club = await member.SendAsync(HttpMethod.Get, "/api/v1/club", null, ct))
		{
			club.StatusCode.ShouldBe(HttpStatusCode.OK);
		}

		using (var seasons = await member.SendAsync(HttpMethod.Get, "/api/v1/seasons", null, ct))
		{
			seasons.StatusCode.ShouldBe(HttpStatusCode.OK);
		}

		using (var a = await member.SendAsync(HttpMethod.Get, $"/api/v1/teams/{teamA}", null, ct))
		{
			a.StatusCode.ShouldBe(HttpStatusCode.OK);
		}

		using (var b = await member.SendAsync(HttpMethod.Get, $"/api/v1/teams/{teamB}", null, ct))
		{
			b.StatusCode.ShouldBe(HttpStatusCode.OK);
		}

		await using (var provider = PlatformServices.Build(db))
		await using (var scope = provider.CreateAsyncScope())
		{
			var context = scope.ServiceProvider.GetRequiredService<TestRequestContext>();
			context.ActorKind = AuditActorKind.Member;
			context.MemberAccountId = memberId;
			var auth = scope.ServiceProvider.GetRequiredService<IAccessAuthorizer>();
			(await auth.AuthorizeTeamResourceAsync(new("team", teamA), TeamPermission.Write, ct)).IsGranted.ShouldBeTrue();
			(await auth.AuthorizeTeamResourceAsync(new("team", teamB), TeamPermission.Write, ct)).Kind.ShouldBe(AccessDecisionKind.Forbidden);
		}

		var denialsBefore = await ScalarAsync<long>(db, "SELECT count(*) FROM socalytics.security_audit_event WHERE event_type = 'authorization.denied'", ct);
		using (var c = await member.SendAsync(HttpMethod.Get, $"/api/v1/teams/{teamC}", null, ct))
		{
			c.StatusCode.ShouldBe(HttpStatusCode.NotFound);
		}

		(await ScalarAsync<long>(db, "SELECT count(*) FROM socalytics.security_audit_event WHERE event_type = 'authorization.denied'", ct)).ShouldBe(denialsBefore + 1);

		using (var revoked = await admin.SendAsync(HttpMethod.Delete, RolePath(memberId, teamA), null, ct))
		{
			revoked.StatusCode.ShouldBe(HttpStatusCode.OK);
		}

		using (var gone = await member.SendAsync(HttpMethod.Get, $"/api/v1/teams/{teamA}", null, ct))
		{
			gone.StatusCode.ShouldBe(HttpStatusCode.NotFound);
		}

		(await ScalarAsync<long>(db, "SELECT count(*) FROM socalytics.security_audit_event WHERE event_type = 'team-role.revoked' AND outcome = 'succeeded'", ct)).ShouldBe(1);
		using (var absent = await admin.SendAsync(HttpMethod.Delete, RolePath(memberId, teamA), null, ct))
		{
			absent.StatusCode.ShouldBe(HttpStatusCode.OK);
		}

		(await ScalarAsync<long>(db, "SELECT count(*) FROM socalytics.security_audit_event WHERE event_type = 'team-role.revoked' AND outcome = 'unchanged'", ct)).ShouldBe(1);

		(await ListedTeamsAsync(admin, ct)).ShouldBe([teamA, teamB, teamC], ignoreOrder: true);
	}

	[Fact]
	public async Task ReplacingRolesAndRejections()
	{
		var ct = TestContext.Current.CancellationToken;
		var (db, factory) = await StartAsync(ct);
		await using var _ = db;
		await using var __ = factory;
		await TestMembers.SeedAsync(db, "role-admin", clubRoles: ["club-admin"], cancellationToken: ct);
		var memberId = await TestMembers.SeedAsync(db, "role-member", cancellationToken: ct);
		var inactiveId = await TestMembers.SeedAsync(db, "role-gone", membershipStatus: "deactivated", cancellationToken: ct);
		using var admin = await ApiSession.SignInAsync(factory, "role-admin", TestMembers.DefaultPassword, ct);
		using var member = await ApiSession.SignInAsync(factory, "role-member", TestMembers.DefaultPassword, ct);

		using var seasonResponse = await admin.SendAsync(HttpMethod.Post, "/api/v1/seasons", new { name = "2026" }, ct);
		var season = (await seasonResponse.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("id").GetGuid();
		var team = await CreateTeamAsync(admin, season, "A", ct);

		(await AssignAsync(admin, memberId, team, "coach", ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
		(await AssignAsync(admin, memberId, team, "viewer", ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
		(await ScalarAsync<string>(db, $"SELECT string_agg(role, ',') FROM socalytics.team_role_assignment WHERE member_account_id = '{memberId}'", ct)).ShouldBe("viewer");
		(await ScalarAsync<long>(db, "SELECT count(*) FROM socalytics.security_audit_event WHERE event_type = 'team-role.assigned' AND details->>'previousRole' = 'coach'", ct)).ShouldBe(1);

		using (var invalid = await AssignAsync(admin, memberId, team, "owner", ct))
		{
			invalid.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
		}

		using (var inactive = await AssignAsync(admin, inactiveId, team, "coach", ct))
		{
			inactive.StatusCode.ShouldBe(HttpStatusCode.Conflict);
			(await inactive.Content.ReadAsStringAsync(ct)).ShouldContain("membership-inactive");
		}

		using (var noTeam = await AssignAsync(admin, memberId, Guid.NewGuid(), "coach", ct))
		{
			noTeam.StatusCode.ShouldBe(HttpStatusCode.NotFound);
		}

		using (var noMember = await AssignAsync(admin, Guid.NewGuid(), team, "coach", ct))
		{
			noMember.StatusCode.ShouldBe(HttpStatusCode.NotFound);
		}

		using (var forbidden = await AssignAsync(member, memberId, team, "coach", ct))
		{
			forbidden.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
		}
	}
}
