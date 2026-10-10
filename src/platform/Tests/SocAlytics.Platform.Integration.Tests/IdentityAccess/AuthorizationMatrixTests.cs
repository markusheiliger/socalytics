using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;
using Shouldly;
using SocAlytics.Platform.Integration.Tests.IdentityAccess.Support;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Migrator;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.IdentityAccess;

public sealed class AuthorizationMatrixTests(PostgresContainerFixture postgres)
{
	private static readonly Dictionary<string, string?> Config = new()
	{
		["ClubDisplayName"] = "Matrix Club",
		["FirstClubAdmin:AccountName"] = "mx-bootstrap",
		["FirstClubAdmin:InitialPassword"] = "Initial-Admin-Pass-1234",
	};

	private const string TeamBName = "Secret-Team-B";
	private const string TeamBOpponent = "Secret-Opponent-B";

	private enum Caller { Admin, Registrar, Coach, Viewer, NoRole, PasswordChange, Revoked, Deactivated, Unauthenticated }

	private enum Target { TeamA, TeamB, Unknown }

	private enum Gate { Session, Read, Club, Team }

	private sealed record Ids(Guid Season, Guid TeamA, Guid TeamB, Guid MatchA, Guid MatchB, Guid Victim, Guid Unknown);

	private sealed record Operation(string Id, HttpMethod Method, string Path, Gate Gate, bool AdminRuns = false);

	private static readonly Operation[] Operations =
	[
		new("getSession", HttpMethod.Get, "/api/v1/session", Gate.Session),
		new("getCurrentMember", HttpMethod.Get, "/api/v1/me", Gate.Session),
		new("getClub", HttpMethod.Get, "/api/v1/club", Gate.Read),
		new("listSeasons", HttpMethod.Get, "/api/v1/seasons", Gate.Read),
		new("getSeason", HttpMethod.Get, "/api/v1/seasons/{season}", Gate.Read),
		new("listSeasonTeams", HttpMethod.Get, "/api/v1/seasons/{season}/teams", Gate.Read),
		new("listTeams", HttpMethod.Get, "/api/v1/teams", Gate.Read),
		new("updateClubSettings", HttpMethod.Put, "/api/v1/club", Gate.Club, true),
		new("createSeason", HttpMethod.Post, "/api/v1/seasons", Gate.Club, true),
		new("activateSeason", HttpMethod.Post, "/api/v1/seasons/{season}/activate", Gate.Club),
		new("archiveSeason", HttpMethod.Post, "/api/v1/seasons/{season}/archive", Gate.Club),
		new("createTeam", HttpMethod.Post, "/api/v1/seasons/{season}/teams", Gate.Club, true),
		new("updateTeam", HttpMethod.Put, "/api/v1/teams/{team}", Gate.Club, true),
		new("listMembers", HttpMethod.Get, "/api/v1/members", Gate.Club),
		new("getMember", HttpMethod.Get, "/api/v1/members/{member}", Gate.Club),
		new("createMember", HttpMethod.Post, "/api/v1/members", Gate.Club),
		new("deactivateMember", HttpMethod.Post, "/api/v1/members/{member}/deactivate", Gate.Club),
		new("reactivateMember", HttpMethod.Post, "/api/v1/members/{member}/reactivate", Gate.Club),
		new("unlockMember", HttpMethod.Post, "/api/v1/members/{member}/unlock", Gate.Club),
		new("assignClubRole", HttpMethod.Put, "/api/v1/members/{member}/club-roles/registrar", Gate.Club),
		new("revokeClubRole", HttpMethod.Delete, "/api/v1/members/{member}/club-roles/registrar", Gate.Club),
		new("assignTeamRole", HttpMethod.Put, "/api/v1/members/{member}/team-roles/{team}", Gate.Club),
		new("revokeTeamRole", HttpMethod.Delete, "/api/v1/members/{member}/team-roles/{team}", Gate.Club),
		new("issueCredential", HttpMethod.Post, "/api/v1/members/{member}/credentials", Gate.Club),
		new("endMemberSessions", HttpMethod.Delete, "/api/v1/members/{member}/sessions", Gate.Club),
		new("getTeam", HttpMethod.Get, "/api/v1/teams/{team}", Gate.Team, true),
		new("listTeamMatches", HttpMethod.Get, "/api/v1/teams/{team}/matches", Gate.Team, true),
		new("createMatch", HttpMethod.Post, "/api/v1/teams/{team}/matches", Gate.Team, true),
		new("getMatch", HttpMethod.Get, "/api/v1/matches/{match}", Gate.Team, true),
		new("updateMatch", HttpMethod.Put, "/api/v1/matches/{match}", Gate.Team, true),
	];

	private static readonly string[] PasswordChangeAllowed = ["getSession", "getCurrentMember"];

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

	private static Task<string> SnapshotAsync(IsolatedDatabase db, CancellationToken ct)
	{
		var tables = new[] { "club", "season", "team", "match", "member_account", "club_role_assignment", "team_role_assignment", "one_time_credential" };
		var parts = string.Join(", ", tables.Select(t =>
			$"(SELECT md5(coalesce(string_agg(x::text, ',' ORDER BY x::text), '')) FROM socalytics.{t} x)"));
		return ScalarAsync<string>(db, $"SELECT md5(concat_ws('|', {parts}))", ct);
	}

	private static async Task<Guid> CreateAsync(ApiSession admin, string path, object body, CancellationToken ct)
	{
		using var response = await admin.SendAsync(HttpMethod.Post, path, body, ct);
		response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(ct));
		return (await response.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("id").GetGuid();
	}

	private static async Task<string> EtagAsync(ApiSession admin, string path, CancellationToken ct)
	{
		using var response = await admin.SendAsync(HttpMethod.Get, path, null, ct);
		return response.Headers.ETag?.Tag ?? "\"1\"";
	}

	private static async Task GrantTeamRoleAsync(ApiSession admin, Guid member, Guid team, string role, CancellationToken ct)
	{
		using var response = await admin.SendAsync(HttpMethod.Put, $"/api/v1/members/{member}/team-roles/{team}", new { role }, ct);
		response.StatusCode.ShouldBe(HttpStatusCode.OK);
	}

	private static int Expected(Caller caller, Operation op, Target target)
	{
		if (caller is Caller.Unauthenticated or Caller.Deactivated)
		{
			return 401;
		}

		if (caller == Caller.PasswordChange)
		{
			return PasswordChangeAllowed.Contains(op.Id) ? 200 : 403;
		}

		var created = op.Method == HttpMethod.Post;
		switch (op.Gate)
		{
			case Gate.Session:
				return 200;
			case Gate.Read:
				return target == Target.Unknown && (op.Id is "getSeason" or "listSeasonTeams") ? 404 : 200;
			case Gate.Club:
				if (caller != Caller.Admin)
				{
					return 403;
				}

				if (target == Target.Unknown && op.Id is "createTeam" or "updateTeam")
				{
					return 404;
				}

				return created ? 201 : 200;
			default:
				if (target == Target.Unknown)
				{
					return 404;
				}

				if (caller == Caller.Admin)
				{
					return created ? 201 : 200;
				}

				var visible = target == Target.TeamA && caller is Caller.Coach or Caller.Viewer;
				if (!visible)
				{
					return 404;
				}

				var write = op.Id is "createMatch" or "updateMatch";
				if (write && caller != Caller.Coach)
				{
					return 403;
				}

				return created ? 201 : 200;
		}
	}

	private static string Resolve(Operation op, Ids ids, Target target)
	{
		var unknown = target == Target.Unknown;
		return op.Path
			.Replace("{season}", (unknown ? ids.Unknown : ids.Season).ToString(), StringComparison.Ordinal)
			.Replace("{team}", (unknown ? ids.Unknown : target == Target.TeamA ? ids.TeamA : ids.TeamB).ToString(), StringComparison.Ordinal)
			.Replace("{match}", (unknown ? ids.Unknown : target == Target.TeamA ? ids.MatchA : ids.MatchB).ToString(), StringComparison.Ordinal)
			.Replace("{member}", (unknown ? ids.Unknown : ids.Victim).ToString(), StringComparison.Ordinal);
	}

	private static object? Body(Operation op) => op.Id switch
	{
		"updateClubSettings" => new { displayName = "Matrix Club" },
		"createSeason" => new { name = "S-" + Guid.NewGuid().ToString("N")[..8] },
		"createTeam" => new { name = "T-" + Guid.NewGuid().ToString("N")[..8] },
		"updateTeam" => new { name = "Matrix A" },
		"createMember" => new { accountName = "m-" + Guid.NewGuid().ToString("N")[..10] },
		"assignTeamRole" => new { role = "viewer" },
		"issueCredential" => new { purpose = "password-reset" },
		"createMatch" => new { opponent = new { name = "Opp" }, kickoffAt = "2026-05-01T10:00:00Z", homeAway = "home", competition = "League" },
		"updateMatch" => new { kickoffAt = "2026-05-01T10:00:00Z", homeAway = "home", competition = "League" },
		_ => null,
	};

	private static async Task<(int Status, string Body)> SendAsync(
		HttpClient client, string csrf, Operation op, string path, string? ifMatch, CancellationToken ct)
	{
		using var request = new HttpRequestMessage(op.Method, path);
		var body = Body(op);
		if (body is not null)
		{
			request.Content = JsonContent.Create(body);
		}

		if (op.Method != HttpMethod.Get)
		{
			request.Headers.Add("X-CSRF-Token", csrf);
		}

		if (ifMatch is not null)
		{
			request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
		}

		using var response = await client.SendAsync(request, ct);
		return ((int)response.StatusCode, await response.Content.ReadAsStringAsync(ct));
	}

	[Fact]
	public async Task AuthorizationMatrixAllowsOnlyPermittedCombinationsAndLeaksNothing()
	{
		var ct = TestContext.Current.CancellationToken;
		var (db, factory) = await StartAsync(ct);
		await using var _ = db;
		await using var __ = factory;
		await TestMembers.SeedAsync(db, "mx-admin", clubRoles: ["club-admin"], cancellationToken: ct);
		await TestMembers.SeedAsync(db, "mx-registrar", clubRoles: ["registrar"], cancellationToken: ct);
		var coach = await TestMembers.SeedAsync(db, "mx-coach", cancellationToken: ct);
		var viewer = await TestMembers.SeedAsync(db, "mx-viewer", cancellationToken: ct);
		await TestMembers.SeedAsync(db, "mx-norole", cancellationToken: ct);
		await TestMembers.SeedAsync(db, "mx-pcr", passwordChangeRequired: true, cancellationToken: ct);
		var revoked = await TestMembers.SeedAsync(db, "mx-revoked", cancellationToken: ct);
		var deactivated = await TestMembers.SeedAsync(db, "mx-deactivated", cancellationToken: ct);
		var victim = await TestMembers.SeedAsync(db, "mx-victim", cancellationToken: ct);

		using var admin = await ApiSession.SignInAsync(factory, "mx-admin", TestMembers.DefaultPassword, ct);
		var season = await CreateAsync(admin, "/api/v1/seasons", new { name = "Matrix" }, ct);
		var teamA = await CreateAsync(admin, $"/api/v1/seasons/{season}/teams", new { name = "Matrix A" }, ct);
		var teamB = await CreateAsync(admin, $"/api/v1/seasons/{season}/teams", new { name = TeamBName }, ct);
		var matchA = await CreateAsync(admin, $"/api/v1/teams/{teamA}/matches", new { opponent = new { name = "Opp" }, kickoffAt = "2026-05-01T10:00:00Z", homeAway = "home", competition = "League" }, ct);
		var matchB = await CreateAsync(admin, $"/api/v1/teams/{teamB}/matches", new { opponent = new { name = TeamBOpponent }, kickoffAt = "2026-05-01T10:00:00Z", homeAway = "home", competition = "League" }, ct);
		var ids = new Ids(season, teamA, teamB, matchA, matchB, victim, Guid.NewGuid());

		await GrantTeamRoleAsync(admin, coach, teamA, "coach", ct);
		await GrantTeamRoleAsync(admin, viewer, teamA, "viewer", ct);
		await GrantTeamRoleAsync(admin, revoked, teamA, "coach", ct);

		var sessions = new Dictionary<Caller, ApiSession?>
		{
			[Caller.Admin] = admin,
			[Caller.Registrar] = await ApiSession.SignInAsync(factory, "mx-registrar", TestMembers.DefaultPassword, ct),
			[Caller.Coach] = await ApiSession.SignInAsync(factory, "mx-coach", TestMembers.DefaultPassword, ct),
			[Caller.Viewer] = await ApiSession.SignInAsync(factory, "mx-viewer", TestMembers.DefaultPassword, ct),
			[Caller.NoRole] = await ApiSession.SignInAsync(factory, "mx-norole", TestMembers.DefaultPassword, ct),
			[Caller.PasswordChange] = await ApiSession.SignInAsync(factory, "mx-pcr", TestMembers.DefaultPassword, ct),
			[Caller.Revoked] = await ApiSession.SignInAsync(factory, "mx-revoked", TestMembers.DefaultPassword, ct),
			[Caller.Deactivated] = await ApiSession.SignInAsync(factory, "mx-deactivated", TestMembers.DefaultPassword, ct),
			[Caller.Unauthenticated] = null,
		};

		using (var response = await admin.SendAsync(HttpMethod.Delete, $"/api/v1/members/{revoked}/team-roles/{teamA}", null, ct))
		{
			response.StatusCode.ShouldBe(HttpStatusCode.OK);
		}

		using (var response = await admin.SendAsync(HttpMethod.Post, $"/api/v1/members/{deactivated}/deactivate", null, ct))
		{
			response.StatusCode.ShouldBe(HttpStatusCode.OK);
		}

		using var anonymous = factory.CreateApiClient();
		var failures = new List<string>();
		string[] secrets = [teamB.ToString(), matchB.ToString(), TeamBName, TeamBOpponent];

		foreach (var (caller, session) in sessions)
		{
			var client = session?.Client ?? anonymous;
			var csrf = session?.AntiforgeryToken ?? "unauthenticated";
			var seen = new HashSet<string>();
			foreach (var op in Operations)
			{
				if (caller == Caller.Admin && !op.AdminRuns && op.Gate == Gate.Club)
				{
					continue;
				}

				foreach (var target in Enum.GetValues<Target>())
				{
					var path = Resolve(op, ids, target);
					if (!seen.Add(op.Id + path))
					{
						continue;
					}

					var expected = Expected(caller, op, target);
					string? ifMatch = null;
					if (op.Id == "updateClubSettings" || op.Id == "updateTeam" || op.Id == "updateMatch")
					{
						ifMatch = await EtagAsync(admin, op.Id == "updateClubSettings" ? "/api/v1/club" : path, ct);
					}

					var denied = expected >= 400;
					var before = denied ? await SnapshotAsync(db, ct) : null;
					var (status, body) = await SendAsync(client, csrf, op, path, ifMatch, ct);
					var label = $"{caller} {op.Id} {target}";
					if (status != expected)
					{
						failures.Add($"{label}: expected {expected}, got {status}");
					}

					if (denied && status >= 400 && await SnapshotAsync(db, ct) != before)
					{
						failures.Add($"{label}: denied request changed data");
					}

					if (caller == Caller.PasswordChange && expected == 403 && !body.Contains("password-change-required", StringComparison.Ordinal))
					{
						failures.Add($"{label}: missing password-change-required");
					}

					if (caller != Caller.Admin)
					{
						foreach (var secret in secrets.Where(s => body.Contains(s, StringComparison.Ordinal)))
						{
							failures.Add($"{label}: body leaks {secret}");
						}
					}
				}
			}
		}

		failures.ShouldBeEmpty(string.Join(Environment.NewLine, failures));
	}

	[Fact]
	public async Task RestrictedCallerMayStillUseSelfServiceOperations()
	{
		var ct = TestContext.Current.CancellationToken;
		var (db, factory) = await StartAsync(ct);
		await using var _ = db;
		await using var __ = factory;
		await TestMembers.SeedAsync(db, "mx-pcr", passwordChangeRequired: true, cancellationToken: ct);

		using (var pcr = await ApiSession.SignInAsync(factory, "mx-pcr", TestMembers.DefaultPassword, ct))
		{
			using var signOut = await pcr.SendAsync(HttpMethod.Delete, "/api/v1/session", null, ct);
			signOut.StatusCode.ShouldBe(HttpStatusCode.NoContent);
		}

		using var again = await ApiSession.SignInAsync(factory, "mx-pcr", TestMembers.DefaultPassword, ct);
		using var change = await again.SendAsync(HttpMethod.Post, "/api/v1/me/password",
			new { currentPassword = TestMembers.DefaultPassword, newPassword = "Another-Strong-Pass-99" }, ct);
		change.StatusCode.ShouldBe(HttpStatusCode.NoContent, await change.Content.ReadAsStringAsync(ct));
	}

	[Fact]
	public async Task RevocationsTakeEffectOnTheNextRequest()
	{
		var ct = TestContext.Current.CancellationToken;
		var (db, factory) = await StartAsync(ct);
		await using var _ = db;
		await using var __ = factory;
		await TestMembers.SeedAsync(db, "rv-admin", clubRoles: ["club-admin"], cancellationToken: ct);
		var secondAdmin = await TestMembers.SeedAsync(db, "rv-admin2", clubRoles: ["club-admin"], cancellationToken: ct);
		var coach = await TestMembers.SeedAsync(db, "rv-coach", cancellationToken: ct);
		var gone = await TestMembers.SeedAsync(db, "rv-gone", cancellationToken: ct);
		var ended = await TestMembers.SeedAsync(db, "rv-ended", cancellationToken: ct);
		using var admin = await ApiSession.SignInAsync(factory, "rv-admin", TestMembers.DefaultPassword, ct);
		var season = await CreateAsync(admin, "/api/v1/seasons", new { name = "R" }, ct);
		var team = await CreateAsync(admin, $"/api/v1/seasons/{season}/teams", new { name = "R A" }, ct);
		await GrantTeamRoleAsync(admin, coach, team, "coach", ct);

		using var coachSession = await ApiSession.SignInAsync(factory, "rv-coach", TestMembers.DefaultPassword, ct);
		using var admin2 = await ApiSession.SignInAsync(factory, "rv-admin2", TestMembers.DefaultPassword, ct);
		using var goneSession = await ApiSession.SignInAsync(factory, "rv-gone", TestMembers.DefaultPassword, ct);
		using var endedSession = await ApiSession.SignInAsync(factory, "rv-ended", TestMembers.DefaultPassword, ct);

		async Task<HttpStatusCode> StatusAsync(ApiSession s, string path)
		{
			using var response = await s.SendAsync(HttpMethod.Get, path, null, ct);
			return response.StatusCode;
		}

		(await StatusAsync(coachSession, $"/api/v1/teams/{team}")).ShouldBe(HttpStatusCode.OK);
		(await StatusAsync(admin2, "/api/v1/members")).ShouldBe(HttpStatusCode.OK);
		(await StatusAsync(goneSession, "/api/v1/club")).ShouldBe(HttpStatusCode.OK);
		(await StatusAsync(endedSession, "/api/v1/club")).ShouldBe(HttpStatusCode.OK);

		using (var r = await admin.SendAsync(HttpMethod.Delete, $"/api/v1/members/{coach}/team-roles/{team}", null, ct))
		{
			r.StatusCode.ShouldBe(HttpStatusCode.OK);
		}

		using (var r = await admin.SendAsync(HttpMethod.Delete, $"/api/v1/members/{secondAdmin}/club-roles/club-admin", null, ct))
		{
			r.StatusCode.ShouldBe(HttpStatusCode.OK);
		}

		using (var r = await admin.SendAsync(HttpMethod.Post, $"/api/v1/members/{gone}/deactivate", null, ct))
		{
			r.StatusCode.ShouldBe(HttpStatusCode.OK);
		}

		using (var r = await admin.SendAsync(HttpMethod.Delete, $"/api/v1/members/{ended}/sessions", null, ct))
		{
			r.StatusCode.ShouldBe(HttpStatusCode.NoContent);
		}

		(await StatusAsync(coachSession, $"/api/v1/teams/{team}")).ShouldBe(HttpStatusCode.NotFound);
		(await StatusAsync(admin2, "/api/v1/members")).ShouldBe(HttpStatusCode.Forbidden);
		(await StatusAsync(goneSession, "/api/v1/club")).ShouldBe(HttpStatusCode.Unauthorized);
		(await StatusAsync(endedSession, "/api/v1/club")).ShouldBe(HttpStatusCode.Unauthorized);
	}
}
