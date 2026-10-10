using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;
using Shouldly;
using SocAlytics.Platform.Integration.Tests.IdentityAccess.Support;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Migrator;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Club;

public sealed class MatchEndpointTests(PostgresContainerFixture postgres)
{
	private static readonly Dictionary<string, string?> Config = new()
	{
		["ClubDisplayName"] = "Match Club",
		["FirstClubAdmin:AccountName"] = "match-bootstrap",
		["FirstClubAdmin:InitialPassword"] = "Initial-Admin-Pass-1234",
	};

	private static object Body(string opponent = "Rivals") =>
		new { opponent = new { name = opponent }, kickoffAt = "2026-05-01T10:00:00Z", homeAway = "home", competition = "League" };

	private static async Task<Guid> IdAsync(HttpResponseMessage response, CancellationToken ct)
	{
		response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(ct));
		return (await response.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("id").GetGuid();
	}

	private static async Task<long> DeniedCountAsync(IsolatedDatabase db, CancellationToken ct)
	{
		await using var c = new NpgsqlConnection(db.MigratorConnectionString);
		await c.OpenAsync(ct);
		await using var cmd = new NpgsqlCommand("SELECT count(*) FROM socalytics.security_audit_event WHERE event_type = 'authorization.denied'", c);
		return (long)(await cmd.ExecuteScalarAsync(ct))!;
	}

	[Fact]
	public async Task CoachesViewersAndOtherTeamsSeeMatchesAccordingToRole()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var db = await postgres.CreateDatabaseAsync(ct);
		(await MigratorHarness.RunAsync(db, TestMigrationCatalogs.Platform(), ct)).ExitCode.ShouldBe(MigratorExitCode.Success);
		await using var factory = new PlatformApiFactory(db, Config);
		await factory.WaitUntilHealthyAsync(ct);

		await TestMembers.SeedAsync(db, "match-admin", clubRoles: ["club-admin"], cancellationToken: ct);
		var coachId = await TestMembers.SeedAsync(db, "match-coach", cancellationToken: ct);
		var viewerId = await TestMembers.SeedAsync(db, "match-viewer", cancellationToken: ct);
		var otherId = await TestMembers.SeedAsync(db, "match-other", cancellationToken: ct);
		using var admin = await ApiSession.SignInAsync(factory, "match-admin", TestMembers.DefaultPassword, ct);
		using var coach = await ApiSession.SignInAsync(factory, "match-coach", TestMembers.DefaultPassword, ct);
		using var viewer = await ApiSession.SignInAsync(factory, "match-viewer", TestMembers.DefaultPassword, ct);
		using var other = await ApiSession.SignInAsync(factory, "match-other", TestMembers.DefaultPassword, ct);

		using var seasonResponse = await admin.SendAsync(HttpMethod.Post, "/api/v1/seasons", new { name = "2026" }, ct);
		var season = (await seasonResponse.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("id").GetGuid();
		async Task<Guid> TeamAsync(string name)
		{
			using var r = await admin.SendAsync(HttpMethod.Post, $"/api/v1/seasons/{season}/teams", new { name }, ct);
			return await IdAsync(r, ct);
		}

		var teamA = await TeamAsync("A");
		var teamB = await TeamAsync("B");
		foreach (var (member, team, role) in new[] { (coachId, teamA, "coach"), (viewerId, teamA, "viewer"), (otherId, teamB, "coach") })
		{
			using var r = await admin.SendAsync(HttpMethod.Put, $"/api/v1/members/{member}/team-roles/{team}", new { role }, ct);
			r.StatusCode.ShouldBe(HttpStatusCode.OK);
		}

		Guid match;
		using (var created = await coach.SendAsync(HttpMethod.Post, $"/api/v1/teams/{teamA}/matches", Body(), ct))
		{
			created.StatusCode.ShouldBe(HttpStatusCode.Created);
			created.Headers.Location.ShouldNotBeNull();
			created.Headers.ETag.ShouldNotBeNull();
			var json = await created.Content.ReadFromJsonAsync<JsonElement>(ct);
			json.GetProperty("version").GetInt64().ShouldBe(1);
			json.GetProperty("opponent").GetProperty("name").GetString().ShouldBe("Rivals");
			json.GetProperty("homeAway").GetString().ShouldBe("home");
			match = json.GetProperty("id").GetGuid();
		}

		using (var read = await viewer.SendAsync(HttpMethod.Get, $"/api/v1/matches/{match}", null, ct))
		{
			read.StatusCode.ShouldBe(HttpStatusCode.OK);
		}

		using (var list = await viewer.SendAsync(HttpMethod.Get, $"/api/v1/teams/{teamA}/matches", null, ct))
		{
			list.StatusCode.ShouldBe(HttpStatusCode.OK);
			(await list.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("items").GetArrayLength().ShouldBe(1);
		}

		using (var denied = await viewer.SendAsync(HttpMethod.Post, $"/api/v1/teams/{teamA}/matches", Body(), ct))
		{
			denied.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
		}

		var denialsBefore = await DeniedCountAsync(db, ct);
		using (var r = await other.SendAsync(HttpMethod.Get, $"/api/v1/matches/{match}", null, ct))
		{
			r.StatusCode.ShouldBe(HttpStatusCode.NotFound);
		}

		using (var r = await other.SendAsync(HttpMethod.Get, $"/api/v1/teams/{teamA}/matches", null, ct))
		{
			r.StatusCode.ShouldBe(HttpStatusCode.NotFound);
		}

		using (var r = await other.SendAsync(HttpMethod.Post, $"/api/v1/teams/{teamA}/matches", Body(), ct))
		{
			r.StatusCode.ShouldBe(HttpStatusCode.NotFound);
		}

		(await DeniedCountAsync(db, ct)).ShouldBe(denialsBefore + 3);

		using (var adminCreated = await admin.SendAsync(HttpMethod.Post, $"/api/v1/teams/{teamB}/matches", Body("Admin FC"), ct))
		{
			(await IdAsync(adminCreated, ct)).ShouldNotBe(Guid.Empty);
		}

		foreach (var invalid in new object[]
		{
			new { kickoffAt = "2026-05-01T10:00:00Z", homeAway = "home" },
			new { opponent = new { name = "X" }, homeAway = "home" },
			new { opponent = new { name = "X" }, kickoffAt = "2026-05-01T10:00:00Z", homeAway = "sideways" },
			new { opponent = new { name = "X" }, kickoffAt = "2026-05-01T10:00:00+02:00", homeAway = "home" },
			new { opponent = new { name = "X" }, kickoffAt = "nope", homeAway = "home" },
			new { opponent = new { name = new string('x', 101) }, kickoffAt = "2026-05-01T10:00:00Z", homeAway = "home" },
			new { opponent = new { name = "X" }, kickoffAt = "2026-05-01T10:00:00Z", homeAway = "home", competition = new string('c', 101) },
		})
		{
			using var r = await coach.SendAsync(HttpMethod.Post, $"/api/v1/teams/{teamA}/matches", invalid, ct);
			r.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
			(await r.Content.ReadAsStringAsync(ct)).ShouldContain("validation-failed");
		}

		(await admin.SendAsync(HttpMethod.Post, $"/api/v1/seasons/{season}/activate", null, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
		(await admin.SendAsync(HttpMethod.Post, $"/api/v1/seasons/{season}/archive", null, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

		using (var archived = await coach.SendAsync(HttpMethod.Post, $"/api/v1/teams/{teamA}/matches", Body(), ct))
		{
			archived.StatusCode.ShouldBe(HttpStatusCode.Conflict);
			(await archived.Content.ReadAsStringAsync(ct)).ShouldContain("season-archived");
		}

		using (var read = await coach.SendAsync(HttpMethod.Get, $"/api/v1/matches/{match}", null, ct))
		{
			read.StatusCode.ShouldBe(HttpStatusCode.OK);
		}

		using (var list = await coach.SendAsync(HttpMethod.Get, $"/api/v1/teams/{teamA}/matches", null, ct))
		{
			list.StatusCode.ShouldBe(HttpStatusCode.OK);
		}
	}
}
