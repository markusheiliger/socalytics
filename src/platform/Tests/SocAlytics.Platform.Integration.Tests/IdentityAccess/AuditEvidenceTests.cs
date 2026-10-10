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

public sealed class AuditEvidenceTests(PostgresContainerFixture postgres)
{
	private const string BootAdmin = "ev-boot";
	private const string BootPassword = "Evidence-Boot-Pass-1234";
	private const string WrongPassword = "Evidence-Wrong-Pass-0000";
	private const string Temporary = "Evidence-Temporary-Pass-5678";
	private const string Changed = "Evidence-Changed-Pass-91011";
	private const string Redeemed = "Evidence-Redeemed-Pass-1213";

	private static readonly string[] CatalogEventTypes =
	[
		"club.bootstrapped", "club.bootstrap-refused", "club.settings-updated", "session.sign-in", "session.sign-out",
		"account.locked-out", "account.unlocked", "account.password-changed", "credential.issued", "credential.revoked",
		"credential.redeemed", "sessions.ended", "member.created", "member.deactivated", "member.reactivated",
		"club-role.assigned", "club-role.revoked", "team-role.assigned", "team-role.revoked", "season.created",
		"season.activated", "season.archived", "team.created", "team.updated", "match.created", "match.updated",
		"authorization.denied", "break-glass-recovery.applied", "break-glass-recovery.refused",
	];

	private static Dictionary<string, string?> Bootstrap(string account = BootAdmin) => new()
	{
		["ClubDisplayName"] = "Audit Club",
		["FirstClubAdmin:AccountName"] = account,
		["FirstClubAdmin:InitialPassword"] = BootPassword,
	};

	private static Dictionary<string, string?> Directive(string id) => new()
	{
		["AccountName"] = BootAdmin,
		["RecoveryId"] = id,
		["TemporaryCredential"] = Temporary,
	};

	private async Task<IsolatedDatabase> MigratedAsync(CancellationToken ct)
	{
		var db = await postgres.CreateDatabaseAsync(ct);
		(await MigratorHarness.RunAsync(db, TestMigrationCatalogs.Platform(), ct)).ExitCode.ShouldBe(MigratorExitCode.Success);
		return db;
	}

	private static async Task<List<object?[]>> RowsAsync(IsolatedDatabase db, string sql, int columns, CancellationToken ct)
	{
		await using var connection = new NpgsqlConnection(db.MigratorConnectionString);
		await connection.OpenAsync(ct);
		await using var command = new NpgsqlCommand(sql, connection);
		await using var reader = await command.ExecuteReaderAsync(ct);
		var rows = new List<object?[]>();
		while (await reader.ReadAsync(ct))
		{
			var row = new object?[columns];
			for (var i = 0; i < columns; i++)
			{
				row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
			}

			rows.Add(row);
		}

		return rows;
	}

	private static async Task<long> CountAsync(IsolatedDatabase db, string sql, CancellationToken ct) =>
		Convert.ToInt64((await RowsAsync(db, sql, 1, ct))[0][0]);

	private static string LogText(PlatformApiFactory factory) =>
		string.Join('\n', factory.Logs.Entries.Select(e =>
			e.Message + " " + string.Join(' ', e.State.Select(s => s.Key + "=" + s.Value)) + " " + e.Exception));

	private sealed class Evidence
	{
		public List<string> Secrets { get; } = [];

		public List<string> Bodies { get; } = [];

		public List<string> Logs { get; } = [];

		public void Track(string body)
		{
			Bodies.Add(body);
			if (body.Length > 0 && body[0] == '{')
			{
				using var doc = JsonDocument.Parse(body);
				foreach (var name in new[] { "credential", "antiforgeryToken" })
				{
					if (doc.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
					{
						Secrets.Add(value.GetString()!);
					}
				}
			}
		}

		public async Task<JsonElement> CallAsync(
			ApiSession session, HttpMethod method, string path, object? body, HttpStatusCode expected, CancellationToken ct, string? ifMatch = null)
		{
			using var request = new HttpRequestMessage(method, path);
			if (body is not null)
			{
				request.Content = JsonContent.Create(body);
			}

			if (method != HttpMethod.Get)
			{
				request.Headers.Add("X-CSRF-Token", session.AntiforgeryToken);
			}

			if (ifMatch is not null)
			{
				request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
			}

			using var response = await session.Client.SendAsync(request, ct);
			var text = await response.Content.ReadAsStringAsync(ct);
			Track(text);
			response.StatusCode.ShouldBe(expected, $"{method} {path}: {text}");
			Etag = response.Headers.ETag?.Tag;
			return text.Length > 0 && text[0] == '{' ? JsonDocument.Parse(text).RootElement.Clone() : default;
		}

		public string? Etag { get; private set; }

		public async Task<ApiSession> SignInAsync(PlatformApiFactory factory, string name, string password, CancellationToken ct)
		{
			var session = await ApiSession.SignInAsync(factory, name, password, ct);
			Secrets.Add(session.RawToken);
			Secrets.Add(session.AntiforgeryToken);
			Secrets.Add(password);
			return session;
		}

		public async Task<HttpStatusCode> RawAsync(PlatformApiFactory factory, string path, object body, CancellationToken ct)
		{
			using var client = factory.CreateApiClient();
			using var response = await client.PostAsJsonAsync(path, body, ct);
			Track(await response.Content.ReadAsStringAsync(ct));
			return response.StatusCode;
		}
	}

	[Fact]
	public async Task EveryCatalogEventIsRecordedAndNoSecretLeaks()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var db = await MigratedAsync(ct);
		var ev = new Evidence();
		ev.Secrets.AddRange([BootPassword, WrongPassword, Temporary, Changed, Redeemed, TestMembers.DefaultPassword]);

		await using (var factory = new PlatformApiFactory(db, Bootstrap()))
		{
			await factory.WaitUntilHealthyAsync(ct);
			var adminId = await TestMembers.SeedAsync(db, "ev-admin", clubRoles: ["club-admin"], cancellationToken: ct);
			var admin2Id = await TestMembers.SeedAsync(db, "ev-admin2", clubRoles: ["club-admin"], cancellationToken: ct);
			var coachId = await TestMembers.SeedAsync(db, "ev-coach", cancellationToken: ct);
			var lockId = await TestMembers.SeedAsync(db, "ev-lock", cancellationToken: ct);
			var pwdId = await TestMembers.SeedAsync(db, "ev-pwd", cancellationToken: ct);
			var targetId = await TestMembers.SeedAsync(db, "ev-target", cancellationToken: ct);
			var redeemId = await TestMembers.SeedAsync(db, "ev-redeem", cancellationToken: ct);
			var goneId = await TestMembers.SeedAsync(db, "ev-gone", cancellationToken: ct);
			var endedId = await TestMembers.SeedAsync(db, "ev-ended", cancellationToken: ct);
			_ = (adminId, pwdId);

			using var admin = await ev.SignInAsync(factory, "ev-admin", TestMembers.DefaultPassword, ct);
			using var admin2 = await ev.SignInAsync(factory, "ev-admin2", TestMembers.DefaultPassword, ct);
			using var coach = await ev.SignInAsync(factory, "ev-coach", TestMembers.DefaultPassword, ct);
			using var ended = await ev.SignInAsync(factory, "ev-ended", TestMembers.DefaultPassword, ct);

			// club, season, team, match administration
			await ev.CallAsync(admin, HttpMethod.Get, "/api/v1/club", null, HttpStatusCode.OK, ct);
			await ev.CallAsync(admin, HttpMethod.Put, "/api/v1/club", new { displayName = "Audit Club" }, HttpStatusCode.OK, ct, ev.Etag);
			var season = (await ev.CallAsync(admin, HttpMethod.Post, "/api/v1/seasons", new { name = "Audit" }, HttpStatusCode.Created, ct)).GetProperty("id").GetGuid();
			var team = (await ev.CallAsync(admin, HttpMethod.Post, $"/api/v1/seasons/{season}/teams", new { name = "Audit A" }, HttpStatusCode.Created, ct)).GetProperty("id").GetGuid();
			await ev.CallAsync(admin, HttpMethod.Get, $"/api/v1/teams/{team}", null, HttpStatusCode.OK, ct);
			await ev.CallAsync(admin, HttpMethod.Put, $"/api/v1/teams/{team}", new { name = "Audit A2" }, HttpStatusCode.OK, ct, ev.Etag);
			var match = (await ev.CallAsync(admin, HttpMethod.Post, $"/api/v1/teams/{team}/matches",
				new { opponent = new { name = "Opp" }, kickoffAt = "2026-05-01T10:00:00Z", homeAway = "home", competition = "League" }, HttpStatusCode.Created, ct)).GetProperty("id").GetGuid();
			await ev.CallAsync(admin, HttpMethod.Get, $"/api/v1/matches/{match}", null, HttpStatusCode.OK, ct);
			await ev.CallAsync(admin, HttpMethod.Put, $"/api/v1/matches/{match}",
				new { kickoffAt = "2026-05-01T10:00:00Z", homeAway = "away", competition = "League" }, HttpStatusCode.OK, ct, ev.Etag);
			await ev.CallAsync(admin, HttpMethod.Post, $"/api/v1/seasons/{season}/activate", null, HttpStatusCode.OK, ct);
			await ev.CallAsync(admin, HttpMethod.Post, $"/api/v1/seasons/{season}/archive", null, HttpStatusCode.OK, ct);

			// roles
			await ev.CallAsync(admin, HttpMethod.Put, $"/api/v1/members/{coachId}/team-roles/{team}", new { role = "coach" }, HttpStatusCode.OK, ct);
			await ev.CallAsync(admin, HttpMethod.Put, $"/api/v1/members/{targetId}/club-roles/registrar", null, HttpStatusCode.OK, ct);
			await ev.CallAsync(admin, HttpMethod.Delete, $"/api/v1/members/{targetId}/club-roles/registrar", null, HttpStatusCode.OK, ct);
			await ev.CallAsync(admin, HttpMethod.Delete, $"/api/v1/members/{coachId}/team-roles/{team}", null, HttpStatusCode.OK, ct);

			// authorization denial
			await ev.CallAsync(coach, HttpMethod.Post, "/api/v1/seasons", new { name = "Denied" }, HttpStatusCode.Forbidden, ct);

			// members and sessions
			var created = await ev.CallAsync(admin, HttpMethod.Post, "/api/v1/members", new { accountName = "ev-new" }, HttpStatusCode.Created, ct);
			_ = created;
			await ev.CallAsync(admin, HttpMethod.Post, $"/api/v1/members/{goneId}/deactivate", null, HttpStatusCode.OK, ct);
			await ev.CallAsync(admin, HttpMethod.Post, $"/api/v1/members/{goneId}/reactivate", null, HttpStatusCode.OK, ct);
			await ev.CallAsync(admin, HttpMethod.Delete, $"/api/v1/members/{endedId}/sessions", null, HttpStatusCode.NoContent, ct);

			// lockout and unlock
			for (var i = 0; i < 5; i++)
			{
				(await ev.RawAsync(factory, "/api/v1/session", new { accountName = "ev-lock", password = WrongPassword }, ct)).ShouldBe(HttpStatusCode.Unauthorized);
			}

			await ev.CallAsync(admin, HttpMethod.Post, $"/api/v1/members/{lockId}/unlock", null, HttpStatusCode.OK, ct);

			// password change, credential issue and redeem
			using (var pwd = await ev.SignInAsync(factory, "ev-pwd", TestMembers.DefaultPassword, ct))
			{
				await ev.CallAsync(pwd, HttpMethod.Post, "/api/v1/me/password",
					new { currentPassword = TestMembers.DefaultPassword, newPassword = Changed }, HttpStatusCode.NoContent, ct);
				await ev.CallAsync(pwd, HttpMethod.Delete, "/api/v1/session", null, HttpStatusCode.NoContent, ct);
			}

			var issued = await ev.CallAsync(admin, HttpMethod.Post, $"/api/v1/members/{redeemId}/credentials", new { purpose = "password-reset" }, HttpStatusCode.Created, ct);
			(await ev.RawAsync(factory, "/api/v1/credentials/redeem",
				new { accountName = "ev-redeem", credential = issued.GetProperty("credential").GetString(), newPassword = Redeemed }, ct)).IsSuccess().ShouldBeTrue();

			// credential revoked because its issuer loses Club Admin
			await ev.CallAsync(admin2, HttpMethod.Post, $"/api/v1/members/{targetId}/credentials", new { purpose = "password-reset" }, HttpStatusCode.Created, ct);
			await ev.CallAsync(admin, HttpMethod.Delete, $"/api/v1/members/{admin2Id}/club-roles/club-admin", null, HttpStatusCode.OK, ct);

			ev.Logs.Add(LogText(factory));
		}

		// bootstrap refusal on restart with a different first admin
		await using (var conflict = new PlatformApiFactory(db, Bootstrap("ev-other")))
		{
			using var client = conflict.CreateApiClient();
			await Task.Delay(TimeSpan.FromSeconds(3), ct);
			(await conflict.CheckBootstrapAsync(ct)).Description.ShouldBe("bootstrap-conflict");
			ev.Logs.Add(LogText(conflict));
		}

		// break-glass applied, then the same directive refused
		await using (var applied = new PlatformApiFactory(db, Bootstrap(), Directive("ev-recovery-1")))
		{
			await applied.WaitUntilHealthyAsync(ct);
			using var restricted = await ev.SignInAsync(applied, BootAdmin, Temporary, ct);
			await ev.CallAsync(restricted, HttpMethod.Get, "/api/v1/club", null, HttpStatusCode.Forbidden, ct);
			ev.Logs.Add(LogText(applied));
		}

		await using (var refused = new PlatformApiFactory(db, Bootstrap(), Directive("ev-recovery-1")))
		{
			await refused.WaitUntilHealthyAsync(ct);
			ev.Logs.Add(LogText(refused));
		}

		var present = (await RowsAsync(db, "SELECT DISTINCT event_type FROM socalytics.security_audit_event", 1, ct))
			.Select(r => (string)r[0]!).ToHashSet();
		CatalogEventTypes.Except(present).ShouldBeEmpty("event types missing from the audit trail");

		// FR-045 fields
		(await CountAsync(db,
			"SELECT count(*) FROM socalytics.security_audit_event WHERE id IS NULL OR occurred_at IS NULL OR btrim(event_type) = '' " +
			"OR btrim(action) = '' OR btrim(outcome) = '' OR btrim(resource_type) = '' OR btrim(correlation_id) = '' " +
			"OR (actor_kind = 'member' AND actor_account_id IS NULL)", ct)).ShouldBe(0);
		foreach (var type in new[] { "team.created", "team.updated", "match.created", "match.updated", "team-role.assigned", "team-role.revoked" })
		{
			(await CountAsync(db, $"SELECT count(*) FROM socalytics.security_audit_event WHERE event_type = '{type}' AND team_id IS NOT NULL AND resource_id IS NOT NULL", ct))
				.ShouldBeGreaterThan(0, type);
		}

		// secrets produced during the run, including stored hashes, tokens, and stamps
		var secrets = ev.Secrets.ToList();
		foreach (var row in await RowsAsync(db,
			"SELECT password_hash FROM socalytics.member_account WHERE password_hash IS NOT NULL " +
			"UNION ALL SELECT security_stamp FROM socalytics.member_account " +
			"UNION ALL SELECT security_stamp FROM socalytics.member_session " +
			"UNION ALL SELECT encode(token_hash, 'hex') FROM socalytics.member_session " +
			"UNION ALL SELECT encode(token_hash, 'base64') FROM socalytics.member_session " +
			"UNION ALL SELECT encode(credential_hash, 'hex') FROM socalytics.one_time_credential " +
			"UNION ALL SELECT encode(credential_hash, 'base64') FROM socalytics.one_time_credential", 1, ct))
		{
			secrets.Add((string)row[0]!);
		}

		secrets = secrets.Where(s => s.Length >= 8).Distinct().ToList();
		secrets.ShouldNotBeEmpty();
		await using var connection = new NpgsqlConnection(db.MigratorConnectionString);
		await connection.OpenAsync(ct);
		var leaks = new List<string>();
		foreach (var secret in secrets)
		{
			await using var command = new NpgsqlCommand(
				"SELECT count(*) FROM socalytics.security_audit_event e WHERE strpos(e::text, @s) > 0", connection);
			command.Parameters.AddWithValue("s", secret);
			if (Convert.ToInt64(await command.ExecuteScalarAsync(ct)) > 0)
			{
				leaks.Add("audit row contains a secret of length " + secret.Length);
			}

			if (ev.Logs.Any(l => l.Contains(secret, StringComparison.Ordinal)))
			{
				leaks.Add("log contains a secret of length " + secret.Length);
			}

			if (ev.Bodies.Any(b => b.Contains(secret, StringComparison.Ordinal)
				&& !ev.Secrets.Contains(secret)))
			{
				leaks.Add("response body contains a stored secret of length " + secret.Length);
			}
		}

		leaks.ShouldBeEmpty(string.Join(Environment.NewLine, leaks));

		// problem bodies (non-2xx) never carry any secret, including the ones the caller legitimately received
		var problems = ev.Bodies.Where(b => b.Contains("\"status\"", StringComparison.Ordinal)).ToList();
		foreach (var secret in secrets)
		{
			problems.ShouldAllBe(b => !b.Contains(secret, StringComparison.Ordinal));
		}
	}

	[Fact]
	public async Task RejectedLastClubAdminChangeLeavesNeitherChangeNorEvent()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var db = await MigratedAsync(ct);
		await using var factory = new PlatformApiFactory(db);
		var adminId = await TestMembers.SeedAsync(db, "fr-admin", clubRoles: ["club-admin"], cancellationToken: ct);
		var ev = new Evidence();
		using var admin = await ev.SignInAsync(factory, "fr-admin", TestMembers.DefaultPassword, ct);

		await ev.CallAsync(admin, HttpMethod.Delete, $"/api/v1/members/{adminId}/club-roles/club-admin", null, HttpStatusCode.Conflict, ct);

		(await CountAsync(db, "SELECT count(*) FROM socalytics.club_role_assignment WHERE role = 'club-admin'", ct)).ShouldBe(1);
		(await CountAsync(db, "SELECT count(*) FROM socalytics.security_audit_event WHERE event_type = 'club-role.revoked'", ct)).ShouldBe(0);
	}
}

internal static class StatusCodeExtensions
{
	public static bool IsSuccess(this HttpStatusCode code) => (int)code is >= 200 and < 300;
}
