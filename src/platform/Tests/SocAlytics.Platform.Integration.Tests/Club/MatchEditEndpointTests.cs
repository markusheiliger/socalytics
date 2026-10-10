using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Shouldly;
using SocAlytics.Platform.Integration.Tests.IdentityAccess.Support;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Migrator;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Club;

public sealed class MatchEditEndpointTests(PostgresContainerFixture postgres)
{
	private static readonly Dictionary<string, string?> Config = new()
	{
		["ClubDisplayName"] = "Edit Club",
		["FirstClubAdmin:AccountName"] = "edit-bootstrap",
		["FirstClubAdmin:InitialPassword"] = "Initial-Admin-Pass-1234",
	};

	private static object Edit(string competition = "Cup") =>
		new { kickoffAt = "2026-06-01T10:00:00Z", homeAway = "away", competition };

	private static Task<HttpResponseMessage> PutAsync(ApiSession s, Guid match, object body, string? ifMatch, CancellationToken ct)
	{
		var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/matches/{match}") { Content = JsonContent.Create(body) };
		request.Headers.Add("X-CSRF-Token", s.AntiforgeryToken);
		if (ifMatch is not null)
		{
			request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
		}

		return s.Client.SendAsync(request, ct);
	}

	private static async Task<JsonElement> GetAsync(ApiSession s, Guid match, CancellationToken ct)
	{
		using var r = await s.SendAsync(HttpMethod.Get, $"/api/v1/matches/{match}", null, ct);
		return await r.Content.ReadFromJsonAsync<JsonElement>(ct);
	}

	[Fact]
	public async Task MatchEditsAreImmutableVersionedAndBlockedInArchivedSeasons()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var db = await postgres.CreateDatabaseAsync(ct);
		(await MigratorHarness.RunAsync(db, TestMigrationCatalogs.Platform(), ct)).ExitCode.ShouldBe(MigratorExitCode.Success);
		await using var factory = new PlatformApiFactory(db, Config);
		await factory.WaitUntilHealthyAsync(ct);

		await TestMembers.SeedAsync(db, "edit-admin", clubRoles: ["club-admin"], cancellationToken: ct);
		var coachId = await TestMembers.SeedAsync(db, "edit-coach", cancellationToken: ct);
		var coach2Id = await TestMembers.SeedAsync(db, "edit-coach2", cancellationToken: ct);
		var viewerId = await TestMembers.SeedAsync(db, "edit-viewer", cancellationToken: ct);
		var otherId = await TestMembers.SeedAsync(db, "edit-other", cancellationToken: ct);
		using var admin = await ApiSession.SignInAsync(factory, "edit-admin", TestMembers.DefaultPassword, ct);
		using var coach = await ApiSession.SignInAsync(factory, "edit-coach", TestMembers.DefaultPassword, ct);
		using var coach2 = await ApiSession.SignInAsync(factory, "edit-coach2", TestMembers.DefaultPassword, ct);
		using var viewer = await ApiSession.SignInAsync(factory, "edit-viewer", TestMembers.DefaultPassword, ct);
		using var other = await ApiSession.SignInAsync(factory, "edit-other", TestMembers.DefaultPassword, ct);

		using var seasonResponse = await admin.SendAsync(HttpMethod.Post, "/api/v1/seasons", new { name = "2026" }, ct);
		var season = (await seasonResponse.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("id").GetGuid();
		async Task<Guid> TeamAsync(string name)
		{
			using var r = await admin.SendAsync(HttpMethod.Post, $"/api/v1/seasons/{season}/teams", new { name }, ct);
			r.StatusCode.ShouldBe(HttpStatusCode.Created);
			return (await r.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("id").GetGuid();
		}

		var teamA = await TeamAsync("A");
		var teamB = await TeamAsync("B");
		foreach (var (member, team, role) in new[] { (coachId, teamA, "coach"), (coach2Id, teamA, "coach"), (viewerId, teamA, "viewer"), (otherId, teamB, "coach") })
		{
			using var r = await admin.SendAsync(HttpMethod.Put, $"/api/v1/members/{member}/team-roles/{team}", new { role }, ct);
			r.StatusCode.ShouldBe(HttpStatusCode.OK);
		}

		Guid match;
		string etag;
		using (var created = await coach.SendAsync(HttpMethod.Post, $"/api/v1/teams/{teamA}/matches",
			new { opponent = new { name = "Rivals" }, kickoffAt = "2026-05-01T10:00:00Z", homeAway = "home", competition = "League" }, ct))
		{
			created.StatusCode.ShouldBe(HttpStatusCode.Created);
			match = (await created.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("id").GetGuid();
			etag = created.Headers.ETag!.Tag;
		}

		// Immutable fields are rejected and nothing changes (A25).
		foreach (var extra in new object[]
		{
			new { opponent = new { name = "Other" }, kickoffAt = "2026-06-01T10:00:00Z", homeAway = "away" },
			new { teamId = teamB, kickoffAt = "2026-06-01T10:00:00Z", homeAway = "away" },
		})
		{
			using var r = await PutAsync(coach, match, extra, etag, ct);
			r.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
			(await r.Content.ReadAsStringAsync(ct)).ShouldContain("immutable");
		}

		var unchanged = await GetAsync(coach, match, ct);
		unchanged.GetProperty("version").GetInt64().ShouldBe(1);
		unchanged.GetProperty("homeAway").GetString().ShouldBe("home");

		// Missing or wildcard If-Match (A27).
		foreach (var header in new string?[] { null, "*" })
		{
			using var r = await PutAsync(coach, match, Edit(), header, ct);
			r.StatusCode.ShouldBe((HttpStatusCode)428);
		}

		(await GetAsync(coach, match, ct)).GetProperty("version").GetInt64().ShouldBe(1);

		// Two editors with the same ETag (A26).
		using (var first = await PutAsync(coach, match, Edit("First"), etag, ct))
		{
			first.StatusCode.ShouldBe(HttpStatusCode.OK);
			var json = await first.Content.ReadFromJsonAsync<JsonElement>(ct);
			json.GetProperty("version").GetInt64().ShouldBe(2);
			json.GetProperty("opponent").GetProperty("name").GetString().ShouldBe("Rivals");
			first.Headers.ETag.ShouldNotBeNull();
		}

		using (var stale = await PutAsync(coach2, match, Edit("Second"), etag, ct))
		{
			stale.StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);
		}

		var current = await GetAsync(coach, match, ct);
		current.GetProperty("competition").GetString().ShouldBe("First");
		var currentEtag = $"\"{current.GetProperty("version").GetInt64()}\"";

		using (var viewerPut = await PutAsync(viewer, match, Edit(), currentEtag, ct))
		{
			viewerPut.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
		}

		using (var otherPut = await PutAsync(other, match, Edit(), currentEtag, ct))
		{
			otherPut.StatusCode.ShouldBe(HttpStatusCode.NotFound);
		}

		// Archived season (US5-5).
		(await admin.SendAsync(HttpMethod.Post, $"/api/v1/seasons/{season}/activate", null, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
		(await admin.SendAsync(HttpMethod.Post, $"/api/v1/seasons/{season}/archive", null, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
		using (var archived = await PutAsync(coach, match, Edit("Late"), currentEtag, ct))
		{
			archived.StatusCode.ShouldBe(HttpStatusCode.Conflict);
			(await archived.Content.ReadAsStringAsync(ct)).ShouldContain("season-archived");
		}

		(await GetAsync(coach, match, ct)).GetProperty("competition").GetString().ShouldBe("First");
	}
}
