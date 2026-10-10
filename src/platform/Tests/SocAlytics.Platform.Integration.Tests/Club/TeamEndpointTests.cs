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

public sealed class TeamEndpointTests(PostgresContainerFixture postgres)
{
	private static readonly Dictionary<string, string?> Config = new()
	{
		["ClubDisplayName"] = "Team Club",
		["FirstClubAdmin:AccountName"] = "team-bootstrap",
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

	private static async Task<ApiSession> SignInAsync(IsolatedDatabase db, PlatformApiFactory factory, string name, string[]? roles, CancellationToken ct)
	{
		await TestMembers.SeedAsync(db, name, clubRoles: roles, cancellationToken: ct);
		return await ApiSession.SignInAsync(factory, name, TestMembers.DefaultPassword, ct);
	}

	private static async Task<Guid> CreateSeasonAsync(ApiSession admin, string name, CancellationToken ct)
	{
		using var response = await admin.SendAsync(HttpMethod.Post, "/api/v1/seasons", new { name }, ct);
		response.StatusCode.ShouldBe(HttpStatusCode.Created);
		return (await response.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("id").GetGuid();
	}

	private static async Task<(Guid Id, string ETag)> CreateTeamAsync(ApiSession admin, Guid season, string name, CancellationToken ct)
	{
		using var response = await admin.SendAsync(HttpMethod.Post, $"/api/v1/seasons/{season}/teams", new { name }, ct);
		response.StatusCode.ShouldBe(HttpStatusCode.Created);
		var json = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
		json.GetProperty("seasonId").GetGuid().ShouldBe(season);
		return (json.GetProperty("id").GetGuid(), response.Headers.ETag!.Tag);
	}

	private static async Task<HttpResponseMessage> PutAsync(ApiSession s, Guid team, object body, string? ifMatch, CancellationToken ct)
	{
		var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/teams/{team}") { Content = JsonContent.Create(body) };
		request.Headers.Add("X-CSRF-Token", s.AntiforgeryToken);
		if (ifMatch is not null)
		{
			request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
		}

		return await s.Client.SendAsync(request, ct);
	}

	private static async Task<string> NameAsync(IsolatedDatabase db, Guid team, CancellationToken ct)
	{
		await using var c = new NpgsqlConnection(db.AppConnectionString);
		await c.OpenAsync(ct);
		await using var cmd = new NpgsqlCommand("SELECT name FROM socalytics.team WHERE id = @id", c);
		cmd.Parameters.AddWithValue("id", team);
		return (string)(await cmd.ExecuteScalarAsync(ct))!;
	}

	[Fact]
	public async Task CreateUpdateAndArchivedSeasonRules()
	{
		var ct = TestContext.Current.CancellationToken;
		var (db, factory) = await StartAsync(ct);
		await using var _ = db;
		await using var __ = factory;
		using var admin = await SignInAsync(db, factory, "team-admin", ["club-admin"], ct);

		var season = await CreateSeasonAsync(admin, "2026", ct);
		var (team, etag) = await CreateTeamAsync(admin, season, "U12", ct);

		using (var missing = await admin.SendAsync(HttpMethod.Post, $"/api/v1/seasons/{Guid.NewGuid()}/teams", new { name = "X" }, ct))
		{
			missing.StatusCode.ShouldBe(HttpStatusCode.NotFound);
		}

		using (var none = await PutAsync(admin, team, new { name = "U13" }, null, ct))
		{
			none.StatusCode.ShouldBe((HttpStatusCode)428);
		}

		using (var stale = await PutAsync(admin, team, new { name = "U13" }, "\"99\"", ct))
		{
			stale.StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);
		}

		using (var immutable = await PutAsync(admin, team, new { name = "U13", seasonId = Guid.NewGuid() }, etag, ct))
		{
			immutable.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
			(await immutable.Content.ReadAsStringAsync(ct)).ShouldContain("immutable");
		}

		(await NameAsync(db, team, ct)).ShouldBe("U12");

		using (var ok = await PutAsync(admin, team, new { name = "U13" }, etag, ct))
		{
			ok.StatusCode.ShouldBe(HttpStatusCode.OK);
		}

		(await NameAsync(db, team, ct)).ShouldBe("U13");

		(await admin.SendAsync(HttpMethod.Post, $"/api/v1/seasons/{season}/activate", null, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
		(await admin.SendAsync(HttpMethod.Post, $"/api/v1/seasons/{season}/archive", null, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

		using var created = await admin.SendAsync(HttpMethod.Post, $"/api/v1/seasons/{season}/teams", new { name = "Late" }, ct);
		created.StatusCode.ShouldBe(HttpStatusCode.Conflict);
		(await created.Content.ReadAsStringAsync(ct)).ShouldContain("season-archived");

		using var get = await admin.SendAsync(HttpMethod.Get, $"/api/v1/teams/{team}", null, ct);
		get.StatusCode.ShouldBe(HttpStatusCode.OK);
		var json = await get.Content.ReadFromJsonAsync<JsonElement>(ct);
		var current = get.Headers.ETag!.Tag;
		json.GetProperty("seasonState").GetString().ShouldBe("archived");

		using var update = await PutAsync(admin, team, new { name = "Nope" }, current, ct);
		update.StatusCode.ShouldBe(HttpStatusCode.Conflict);
		(await update.Content.ReadAsStringAsync(ct)).ShouldContain("season-archived");
		(await NameAsync(db, team, ct)).ShouldBe("U13");
	}

	[Fact]
	public async Task ConcurrentArchiveAndRenameNeverChangeTeamAfterArchive()
	{
		var ct = TestContext.Current.CancellationToken;
		var (db, factory) = await StartAsync(ct);
		await using var _ = db;
		await using var __ = factory;
		using var admin = await SignInAsync(db, factory, "race-team-admin", ["club-admin"], ct);

		for (var i = 0; i < 20; i++)
		{
			var season = await CreateSeasonAsync(admin, $"S{i}", ct);
			var (team, etag) = await CreateTeamAsync(admin, season, "Before", ct);
			(await admin.SendAsync(HttpMethod.Post, $"/api/v1/seasons/{season}/activate", null, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

			var rename = PutAsync(admin, team, new { name = "After" }, etag, ct);
			var archive = admin.SendAsync(HttpMethod.Post, $"/api/v1/seasons/{season}/archive", null, ct);
			using var r = await rename;
			using var a = await archive;
			a.StatusCode.ShouldBe(HttpStatusCode.OK);
			r.StatusCode.ShouldBeOneOf(HttpStatusCode.OK, HttpStatusCode.Conflict);

			await using var c = new NpgsqlConnection(db.AppConnectionString);
			await c.OpenAsync(ct);
			await using var cmd = new NpgsqlCommand(
				"SELECT t.name, t.version, s.archived_at FROM socalytics.team t JOIN socalytics.season s ON s.id = t.season_id WHERE t.id = @id", c);
			cmd.Parameters.AddWithValue("id", team);
			await using var reader = await cmd.ExecuteReaderAsync(ct);
			(await reader.ReadAsync(ct)).ShouldBeTrue();
			reader.GetString(0).ShouldBe(r.StatusCode == HttpStatusCode.OK ? "After" : "Before");
			reader.IsDBNull(2).ShouldBeFalse();

			// Archive after finishing a successful archive: any later write is rejected.
			using var later = await PutAsync(admin, team, new { name = "Later" }, "\"" + reader.GetInt64(1) + "\"", ct);
			later.StatusCode.ShouldBe(HttpStatusCode.Conflict);
		}
	}

	[Fact]
	public async Task VisibilityAndAuthorization()
	{
		var ct = TestContext.Current.CancellationToken;
		var (db, factory) = await StartAsync(ct);
		await using var _ = db;
		await using var __ = factory;
		using var admin = await SignInAsync(db, factory, "vis-admin", ["club-admin"], ct);
		var coachId = await TestMembers.SeedAsync(db, "vis-coach", cancellationToken: ct);
		using var coach = await ApiSession.SignInAsync(factory, "vis-coach", TestMembers.DefaultPassword, ct);
		using var registrar = await SignInAsync(db, factory, "vis-registrar", ["registrar"], ct);

		var season = await CreateSeasonAsync(admin, "2026", ct);
		var (one, etag) = await CreateTeamAsync(admin, season, "Alpha", ct);
		var (two, _) = await CreateTeamAsync(admin, season, "Beta", ct);

		await using (var c = new NpgsqlConnection(db.AppConnectionString))
		{
			await c.OpenAsync(ct);
			await using var cmd = new NpgsqlCommand(
				"INSERT INTO socalytics.team_role_assignment (member_account_id, team_id, role, assigned_at, assigned_by_account_id) VALUES (@m, @t, 'coach', now(), @m)", c);
			cmd.Parameters.AddWithValue("m", coachId);
			cmd.Parameters.AddWithValue("t", one);
			await cmd.ExecuteNonQueryAsync(ct);
		}

		using var adminList = await admin.SendAsync(HttpMethod.Get, "/api/v1/teams", null, ct);
		(await adminList.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("items").GetArrayLength().ShouldBe(2);

		using var coachList = await coach.SendAsync(HttpMethod.Get, "/api/v1/teams", null, ct);
		var items = (await coachList.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("items");
		items.GetArrayLength().ShouldBe(1);
		items[0].GetProperty("id").GetGuid().ShouldBe(one);

		using var hidden = await coach.SendAsync(HttpMethod.Get, $"/api/v1/teams/{two}", null, ct);
		hidden.StatusCode.ShouldBe(HttpStatusCode.NotFound);

		using var seasonList = await admin.SendAsync(HttpMethod.Get, $"/api/v1/seasons/{Guid.NewGuid()}/teams", null, ct);
		seasonList.StatusCode.ShouldBe(HttpStatusCode.NotFound);

		foreach (var s in new[] { coach, registrar })
		{
			using var create = await s.SendAsync(HttpMethod.Post, $"/api/v1/seasons/{season}/teams", new { name = "Nope" }, ct);
			create.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
			using var update = await PutAsync(s, one, new { name = "Nope" }, etag, ct);
			update.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
		}

		(await NameAsync(db, one, ct)).ShouldBe("Alpha");
	}
}
