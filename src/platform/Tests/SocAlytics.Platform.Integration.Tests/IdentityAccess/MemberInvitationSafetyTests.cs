using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Npgsql;
using Shouldly;
using SocAlytics.Platform.Domain.IdentityAccess;
using SocAlytics.Platform.Integration.Tests.IdentityAccess.Support;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Integration.Tests.Notifications.Support;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.IdentityAccess;

[Collection(MailpitCollection.Name)]
public sealed partial class MemberInvitationSafetyTests(PostgresContainerFixture postgres, MailpitContainerFixture mailpit)
{
	private const string Password = "a-long-enough-password";

	// The tests advance the clock by days, so the admin session must outlive that.
	private static readonly Dictionary<string, string?> LongSessions = new()
	{
		["IdentityAccess:Session:IdleTimeout"] = "30.00:00:00",
		["IdentityAccess:Session:AbsoluteLifetime"] = "30.00:00:00",
	};

	private Task<EmailApiHarness> StartAsync(CancellationToken ct, params (string Key, string Value)[] extra)
	{
		var settings = new Dictionary<string, string?>(LongSessions);
		foreach (var (key, value) in extra)
		{
			settings[key] = value;
		}

		return EmailApiHarness.StartAsync(postgres, mailpit, ct, settings);
	}

	private static async Task<Guid> CreateTeamAsync(EmailApiHarness h, CancellationToken ct)
	{
		using var season = await h.Admin.SendAsync(HttpMethod.Post, "/api/v1/seasons", new { name = "2026" }, ct);
		var seasonId = (await season.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("id").GetGuid();
		using var team = await h.Admin.SendAsync(HttpMethod.Post, $"/api/v1/seasons/{seasonId}/teams", new { name = "U17" }, ct);
		team.StatusCode.ShouldBe(HttpStatusCode.Created);
		return (await team.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("id").GetGuid();
	}

	private static Task<HttpResponseMessage> CreateAsync(ApiSession session, Guid teamId, string accountName, string address, CancellationToken ct) =>
		session.SendAsync(
			HttpMethod.Post,
			"/api/v1/invitations",
			new { address, accountName, teamRoles = new[] { new { teamId, role = "coach" } } },
			ct);

	private sealed record Invited(Guid Id, string Address, string AccountName, long Version);

	private static async Task<Invited> InviteAsync(EmailApiHarness h, Guid teamId, string accountName, string? address, CancellationToken ct)
	{
		address ??= EmailApiHarness.NewAddress();
		using var response = await CreateAsync(h.Admin, teamId, accountName, address, ct);
		response.StatusCode.ShouldBe(HttpStatusCode.Created);
		var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
		return new Invited(body.GetProperty("id").GetGuid(), address, accountName, body.GetProperty("version").GetInt64());
	}

	private static Task<HttpResponseMessage> ActAsync(ApiSession session, string action, Guid id, long? version, CancellationToken ct)
	{
		var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/invitations/{id}/{action}");
		request.Headers.Add("X-CSRF-Token", session.AntiforgeryToken);
		if (version is { } v)
		{
			request.Headers.TryAddWithoutValidation("If-Match", $"\"{v}\"");
		}

		return session.Client.SendAsync(request, ct);
	}

	private static async Task<long> CurrentVersionAsync(EmailApiHarness h, Guid id, CancellationToken ct)
	{
		using var response = await h.Admin.SendAsync(HttpMethod.Get, "/api/v1/invitations", null, ct);
		response.StatusCode.ShouldBe(HttpStatusCode.OK);
		var all = (await response.Content.ReadFromJsonAsync<JsonElement[]>(ct))!;
		return all.Single(i => i.GetProperty("id").GetGuid() == id).GetProperty("version").GetInt64();
	}

	private static async Task<(string Token, string Body)[]> MessagesAsync(EmailApiHarness h, string address, int count, CancellationToken ct)
	{
		await h.DeliverAsync(ct);
		var found = await h.Mailpit.WaitForMessagesAsync($"to:{address}", count, TimeSpan.FromSeconds(30), ct);
		var result = new List<(string, string)>();
		foreach (var message in found)
		{
			var text = await h.Mailpit.GetTextBodyAsync(message.Id, ct);
			result.Add((LinkPattern().Match(text).Groups[1].Value, text));
		}

		return [.. result];
	}

	private static async Task<string> TokenAsync(EmailApiHarness h, string address, CancellationToken ct) =>
		(await MessagesAsync(h, address, 1, ct))[0].Token;

	private static Task<HttpResponseMessage> CheckAsync(EmailApiHarness h, string token, CancellationToken ct) =>
		PostAnonymousAsync(h, "/api/v1/invitations/checks", new { token }, ct);

	private static Task<HttpResponseMessage> AcceptAsync(EmailApiHarness h, string token, string password, CancellationToken ct) =>
		PostAnonymousAsync(h, "/api/v1/invitations/acceptances", new { token, password }, ct);

	private static async Task<HttpResponseMessage> PostAnonymousAsync(EmailApiHarness h, string path, object body, CancellationToken ct)
	{
		using var client = h.Factory.CreateApiClient();
		return await client.PostAsJsonAsync(path, body, ct);
	}

	private static async Task<object?> ScalarAsync(string connectionString, string sql, CancellationToken ct, params (string Name, object Value)[] parameters)
	{
		await using var connection = new NpgsqlConnection(connectionString);
		await connection.OpenAsync(ct);
		await using var command = new NpgsqlCommand(sql, connection);
		foreach (var (name, value) in parameters)
		{
			command.Parameters.AddWithValue(name, value);
		}

		return await command.ExecuteScalarAsync(ct);
	}

	private static async Task<long> CountAsync(EmailApiHarness h, string sql, CancellationToken ct, params (string Name, object Value)[] parameters) =>
		Convert.ToInt64(await ScalarAsync(h.Db.AppConnectionString, sql, ct, parameters));

	private static Task<long> MembersNamedAsync(EmailApiHarness h, string name, CancellationToken ct) =>
		CountAsync(h, "SELECT count(*) FROM socalytics.member_account WHERE normalized_account_name = @n", ct, ("n", name.ToUpperInvariant()));

	private static async Task<long> EventsAsync(
		EmailApiHarness h, string type, CancellationToken ct, string? outcome = null, string? reason = null, string? masked = null) =>
		Convert.ToInt64(await ScalarAsync(
			h.Db.MigratorConnectionString,
			"SELECT count(*) FROM socalytics.security_audit_event WHERE event_type = @t AND (@o::text IS NULL OR outcome = @o) " +
			"AND (@r::text IS NULL OR reason_code = @r) AND (@m::text IS NULL OR details->>'maskedAddress' = @m)",
			ct,
			("t", type),
			("o", (object?)outcome ?? DBNull.Value),
			("r", (object?)reason ?? DBNull.Value),
			("m", (object?)masked ?? DBNull.Value)));

	private static async Task SeedAddressAsync(EmailApiHarness h, Guid memberId, string address, bool verified, CancellationToken ct)
	{
		EmailAddress.TryCreate(address, out var email, out _).ShouldBeTrue();
		await using var connection = new NpgsqlConnection(h.Db.AppConnectionString);
		await connection.OpenAsync(ct);
		await using var command = new NpgsqlCommand(
			"INSERT INTO socalytics.member_email_addresses (account_id, address, address_normalized, state, verified_at, set_by, updated_at) " +
			"VALUES (@id, @a, @n, @s, @v, @id, now())",
			connection);
		command.Parameters.AddWithValue("id", memberId);
		command.Parameters.AddWithValue("a", email!.Value);
		command.Parameters.AddWithValue("n", email.Normalized);
		command.Parameters.AddWithValue("s", verified ? "verified" : "unverified");
		command.Parameters.AddWithValue("v", verified ? DateTimeOffset.UtcNow : DBNull.Value);
		await command.ExecuteNonQueryAsync(ct);
	}

	private static async Task ShouldConflictAsync(HttpResponseMessage response, string code, CancellationToken ct)
	{
		response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
		(await response.Content.ReadAsStringAsync(ct)).ShouldContain(code);
	}

	[Fact]
	// FR-003; SC-003: addresses in use answer 409 email-address-in-use before any message
	public async Task AnAddressInUseIsRefusedBeforeAnyMessage()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var h = await StartAsync(ct);
		var teamId = await CreateTeamAsync(h, ct);
		var addresses = new List<string>();

		foreach (var (name, status, verified) in new[] { ("verified-one", "active", true), ("unverified-one", "active", false), ("deactivated-one", "deactivated", true) })
		{
			var memberId = await TestMembers.SeedAsync(h.Db, name, membershipStatus: status, cancellationToken: ct);
			var address = EmailApiHarness.NewAddress();
			addresses.Add(address);
			await SeedAddressAsync(h, memberId, address, verified, ct);
			using var response = await CreateAsync(h.Admin, teamId, $"new-{name}", address, ct);
			await ShouldConflictAsync(response, "email-address-in-use", ct);
		}

		var pending = await InviteAsync(h, teamId, "pending-holder", null, ct);
		var expired = await InviteAsync(h, teamId, "expired-holder", null, ct);
		await TokenAsync(h, expired.Address, ct);
		h.Factory.Time.Advance(TimeSpan.FromDays(8));
		foreach (var held in new[] { pending, expired })
		{
			using var response = await CreateAsync(h.Admin, teamId, $"other-{held.AccountName}", held.Address, ct);
			await ShouldConflictAsync(response, "email-address-in-use", ct);
		}

		foreach (var address in addresses)
		{
			(await h.MailCountAsync(address, ct)).ShouldBe(0);
		}
	}

	[Fact]
	// FR-003; SC-003: account names of members and of pending or expired invitations answer 409 account-name-taken, also for createMember
	public async Task AnAccountNameInUseIsRefusedBeforeAnyMessageAndForCreateMember()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var h = await StartAsync(ct);
		var teamId = await CreateTeamAsync(h, ct);
		await TestMembers.SeedAsync(h.Db, "member-name", cancellationToken: ct);
		var pending = await InviteAsync(h, teamId, "pending-name", null, ct);
		var expired = await InviteAsync(h, teamId, "expired-name", null, ct);
		await TokenAsync(h, expired.Address, ct);
		h.Factory.Time.Advance(TimeSpan.FromDays(8));

		var fresh = new List<string>();
		foreach (var name in new[] { "member-name", "PENDING-name", "expired-NAME" })
		{
			var address = EmailApiHarness.NewAddress();
			fresh.Add(address);
			using var response = await CreateAsync(h.Admin, teamId, name, address, ct);
			await ShouldConflictAsync(response, "account-name-taken", ct);
		}

		foreach (var held in new[] { pending, expired })
		{
			using var response = await h.Admin.SendAsync(HttpMethod.Post, "/api/v1/members", new { accountName = held.AccountName }, ct);
			await ShouldConflictAsync(response, "account-name-taken", ct);
			(await MembersNamedAsync(h, held.AccountName, ct)).ShouldBe(0);
		}

		foreach (var address in fresh)
		{
			(await h.MailCountAsync(address, ct)).ShouldBe(0);
		}
	}

	[Fact]
	// FR-008, FR-012; SC-004: every ended or unknown token answers the same invitation-invalid problem and creates no member
	public async Task EndedAndUnknownTokensAnswerTheSameProblemAndCreateNoMember()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var h = await StartAsync(ct, ("Invitations:TokenAttemptsPerMinute", "1000"));
		var teamId = await CreateTeamAsync(h, ct);

		var revoked = await InviteAsync(h, teamId, "revoked-name", null, ct);
		var revokedToken = await TokenAsync(h, revoked.Address, ct);
		using (var revoke = await ActAsync(h.Admin, "revoke", revoked.Id, revoked.Version, ct))
		{
			revoke.StatusCode.ShouldBe(HttpStatusCode.OK);
		}

		var superseded = await InviteAsync(h, teamId, "superseded-name", null, ct);
		var first = await TokenAsync(h, superseded.Address, ct);
		using (var resend = await ActAsync(h.Admin, "resend", superseded.Id, superseded.Version, ct))
		{
			resend.StatusCode.ShouldBe(HttpStatusCode.OK);
		}

		(await MessagesAsync(h, superseded.Address, 2, ct)).Length.ShouldBe(2);

		var used = await InviteAsync(h, teamId, "used-name", null, ct);
		var usedToken = await TokenAsync(h, used.Address, ct);
		using (var accepted = await AcceptAsync(h, usedToken, Password, ct))
		{
			accepted.StatusCode.ShouldBe(HttpStatusCode.NoContent);
		}

		var expired = await InviteAsync(h, teamId, "expired-name", null, ct);
		var expiredToken = await TokenAsync(h, expired.Address, ct);
		h.Factory.Time.Advance(TimeSpan.FromDays(8));

		var cases = new[] { expiredToken, revokedToken, first, usedToken, new string('A', 43) };
		var problems = new List<string>();
		foreach (var token in cases)
		{
			foreach (var response in new[] { await CheckAsync(h, token, ct), await AcceptAsync(h, token, Password, ct) })
			{
				using (response)
				{
					response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
					var problem = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))!.AsObject();
					problem.Remove("correlationId");
					problems.Add(problem.ToJsonString());
				}
			}
		}

		problems.ShouldAllBe(p => p.Contains("invitation-invalid"));
		problems.Distinct().Count().ShouldBe(1);
		(await MembersNamedAsync(h, "expired-name", ct)).ShouldBe(0);
		(await MembersNamedAsync(h, "revoked-name", ct)).ShouldBe(0);
		(await MembersNamedAsync(h, "superseded-name", ct)).ShouldBe(0);
		(await MembersNamedAsync(h, "used-name", ct)).ShouldBe(1);
	}

	[Fact]
	// FR-017; SC-005: the eleventh check or acceptance per minute from one client answers 429 with Retry-After
	public async Task TheEleventhCheckOrAcceptancePerMinuteIsRefused()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var h = await StartAsync(ct);
		var token = new string('B', 43);

		for (var i = 0; i < 5; i++)
		{
			using var check = await CheckAsync(h, token, ct);
			check.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
			using var accept = await AcceptAsync(h, token, Password, ct);
			accept.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
		}

		foreach (var response in new[] { await CheckAsync(h, token, ct), await AcceptAsync(h, token, Password, ct) })
		{
			using (response)
			{
				response.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
				response.Headers.RetryAfter.ShouldNotBeNull();
				response.Headers.CacheControl!.NoStore.ShouldBeTrue();
				(await response.Content.ReadAsStringAsync(ct)).ShouldContain("invitation-rate-limited");
			}
		}
	}

	[Fact]
	// FR-019; SC-006: only Club Admins use the operations; everyone else is refused and learns nothing
	public async Task OnlyClubAdminsUseTheOperationsAndOthersLearnNothing()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var h = await StartAsync(ct);
		var teamId = await CreateTeamAsync(h, ct);
		var invited = await InviteAsync(h, teamId, "secret-invitee", null, ct);

		var coachId = await TestMembers.SeedAsync(h.Db, "coach-1", cancellationToken: ct);
		var viewerId = await TestMembers.SeedAsync(h.Db, "viewer-1", cancellationToken: ct);
		await TestMembers.SeedAsync(h.Db, "registrar-1", clubRoles: ["registrar"], cancellationToken: ct);
		foreach (var (memberId, role) in new[] { (coachId, "coach"), (viewerId, "viewer") })
		{
			await using var connection = new NpgsqlConnection(h.Db.AppConnectionString);
			await connection.OpenAsync(ct);
			await using var command = new NpgsqlCommand(
				"INSERT INTO socalytics.team_role_assignment (member_account_id, team_id, role, assigned_at, assigned_by_account_id) VALUES (@m, @t, @r, now(), @m)",
				connection);
			command.Parameters.AddWithValue("m", memberId);
			command.Parameters.AddWithValue("t", teamId);
			command.Parameters.AddWithValue("r", role);
			await command.ExecuteNonQueryAsync(ct);
		}

		var attempted = EmailApiHarness.NewAddress();
		foreach (var name in new[] { "coach-1", "viewer-1", "registrar-1" })
		{
			using var session = await ApiSession.SignInAsync(h.Factory, name, TestMembers.DefaultPassword, ct);
			var responses = new[]
			{
				await CreateAsync(session, teamId, "attempted-name", attempted, ct),
				await session.SendAsync(HttpMethod.Get, "/api/v1/invitations", null, ct),
				await ActAsync(session, "resend", invited.Id, invited.Version, ct),
				await ActAsync(session, "revoke", invited.Id, invited.Version, ct),
			};
			foreach (var response in responses)
			{
				using (response)
				{
					response.StatusCode.ShouldBe(HttpStatusCode.Forbidden, name);
					var body = await response.Content.ReadAsStringAsync(ct);
					body.ShouldNotContain(invited.Address, Case.Insensitive);
					body.ShouldNotContain(invited.AccountName, Case.Insensitive);
				}
			}
		}

		using var anonymous = h.Factory.CreateApiClient();
		using (var list = await anonymous.GetAsync("/api/v1/invitations", ct))
		{
			list.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
		}

		using (var create = await anonymous.PostAsJsonAsync(
			"/api/v1/invitations", new { address = attempted, accountName = "attempted-name", teamRoles = Array.Empty<object>() }, ct))
		{
			create.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
		}

		foreach (var action in new[] { "resend", "revoke" })
		{
			using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/invitations/{invited.Id}/{action}");
			using var response = await anonymous.SendAsync(request, ct);
			response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
		}

		(await CurrentVersionAsync(h, invited.Id, ct)).ShouldBe(invited.Version);
		(await MembersNamedAsync(h, "attempted-name", ct)).ShouldBe(0);
		(await h.MailCountAsync(attempted, ct)).ShouldBe(0);
	}

	[Fact]
	// FR-019; SC-005: one audit event per step with masked addresses, and no secret in audit events, logs, or telemetry
	public async Task AuditTrailHoldsOneEventPerStepAndNothingSecretIsRecorded()
	{
		var ct = TestContext.Current.CancellationToken;
		var telemetry = new StringBuilder();
		using var activities = new ActivityListener
		{
			ShouldListenTo = source => IsScopeOfHost(source.Name),
			Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
			ActivityStopped = activity =>
			{
				lock (telemetry)
				{
					telemetry.Append(activity.DisplayName).Append(' ');
					foreach (var tag in activity.TagObjects)
					{
						telemetry.Append(tag.Key).Append('=').Append(tag.Value).Append(' ');
					}

					foreach (var activityEvent in activity.Events)
					{
						telemetry.Append(activityEvent.Name).Append(' ');
						foreach (var tag in activityEvent.Tags)
						{
							telemetry.Append(tag.Key).Append('=').Append(tag.Value).Append(' ');
						}
					}
				}
			},
		};
		ActivitySource.AddActivityListener(activities);
		using var meters = new MeterListener();
		meters.InstrumentPublished = (instrument, listener) =>
		{
			if (IsScopeOfHost(instrument.Meter.Name))
			{
				listener.EnableMeasurementEvents(instrument);
			}
		};
		void Measured(ReadOnlySpan<KeyValuePair<string, object?>> tags)
		{
			lock (telemetry)
			{
				foreach (var tag in tags)
				{
					telemetry.Append(tag.Key).Append('=').Append(tag.Value).Append(' ');
				}
			}
		}

		meters.SetMeasurementEventCallback<long>((_, _, tags, _) => Measured(tags));
		meters.SetMeasurementEventCallback<int>((_, _, tags, _) => Measured(tags));
		meters.SetMeasurementEventCallback<double>((_, _, tags, _) => Measured(tags));
		meters.Start();

		await using var h = await StartAsync(ct);
		var teamId = await CreateTeamAsync(h, ct);
		// Generated addresses all mask to the same value, so the legitimate link the host's background delivery worker
		// may issue to audit-c was counted with the first invitee's; this address masks differently (a***@i***.test).
		// The probe of the cause was not run; the cause is the one analysed in the spec's research R1.
		var a = await InviteAsync(h, teamId, "audit-a", $"audit-a-{Guid.NewGuid():N}@invitee.test", ct);
		EmailAddress.TryCreate(a.Address, out var addressA, out _).ShouldBeTrue();

		var firstMessage = (await MessagesAsync(h, a.Address, 1, ct))[0];
		using (var resend = await ActAsync(h.Admin, "resend", a.Id, a.Version, ct))
		{
			resend.StatusCode.ShouldBe(HttpStatusCode.OK);
		}

		var messages = await MessagesAsync(h, a.Address, 2, ct);
		var second = messages.Single(m => m.Token != firstMessage.Token);

		using (var superseded = await AcceptAsync(h, firstMessage.Token, Password, ct))
		{
			superseded.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
		}

		const string weakPassword = "short";
		using (var weak = await AcceptAsync(h, second.Token, weakPassword, ct))
		{
			weak.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
		}

		using (var accepted = await AcceptAsync(h, second.Token, Password, ct))
		{
			accepted.StatusCode.ShouldBe(HttpStatusCode.NoContent);
		}

		using (var again = await AcceptAsync(h, second.Token, Password, ct))
		{
			again.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
		}

		using (var duplicate = await CreateAsync(h.Admin, teamId, "audit-b", a.Address, ct))
		{
			await ShouldConflictAsync(duplicate, "email-address-in-use", ct);
		}

		var c = await InviteAsync(h, teamId, "audit-c", null, ct);
		using (var revoke = await ActAsync(h.Admin, "revoke", c.Id, c.Version, ct))
		{
			revoke.StatusCode.ShouldBe(HttpStatusCode.OK);
		}

		var revokedVersion = await CurrentVersionAsync(h, c.Id, ct);
		foreach (var action in new[] { "revoke", "resend" })
		{
			using var refused = await ActAsync(h.Admin, action, c.Id, revokedVersion, ct);
			await ShouldConflictAsync(refused, "invitation-not-open", ct);
		}

		(await EventsAsync(h, "invitation.created", ct, "succeeded")).ShouldBe(2);
		(await EventsAsync(h, "invitation.created", ct, "refused", "email-address-in-use")).ShouldBe(1);
		(await EventsAsync(h, "invitation.issued", ct, masked: addressA!.Masked)).ShouldBe(2);
		(await EventsAsync(h, "invitation.resent", ct, "succeeded")).ShouldBe(1);
		(await EventsAsync(h, "invitation.resent", ct, "refused", "invitation-not-open")).ShouldBe(1);
		(await EventsAsync(h, "invitation.revoked", ct, "succeeded")).ShouldBe(1);
		(await EventsAsync(h, "invitation.revoked", ct, "refused", "invitation-not-open")).ShouldBe(1);
		(await EventsAsync(h, "invitation.accepted", ct, "succeeded", masked: addressA.Masked)).ShouldBe(1);
		(await EventsAsync(h, "invitation.acceptance-refused", ct)).ShouldBe(3);
		(await EventsAsync(h, "invitation.acceptance-refused", ct, reason: "invitation-invalid")).ShouldBe(2);
		(await EventsAsync(h, "invitation.acceptance-refused", ct, reason: "password-policy")).ShouldBe(1);
		(await ScalarAsync(
			h.Db.MigratorConnectionString,
			"SELECT count(*) FROM socalytics.security_audit_event WHERE event_type LIKE 'invitation.%' " +
			"AND event_type NOT IN ('invitation.acceptance-refused') AND details->>'maskedAddress' IS NULL",
			ct)).ShouldBe(0L);

		var audit = (string?)await ScalarAsync(
			h.Db.MigratorConnectionString,
			"SELECT coalesce(string_agg(row_to_json(e)::text, ' '), '') FROM socalytics.security_audit_event e",
			ct);
		var logs = string.Join(
			' ',
			h.Factory.Logs.Entries.Select(e => $"{e.Message} {e.Exception} {string.Join(' ', e.State.Select(p => $"{p.Key}={p.Value}"))}"));
		string telemetryText;
		lock (telemetry)
		{
			telemetryText = telemetry.ToString();
		}

		var secrets = new[]
		{
			a.Address, addressA.Normalized, c.Address, firstMessage.Token, second.Token, Password, weakPassword,
			TestMembers.DefaultPassword, firstMessage.Body, second.Body,
		};
		foreach (var (name, text) in new[] { ("audit", audit!), ("logs", logs), ("telemetry", telemetryText) })
		{
			foreach (var secret in secrets)
			{
				text.ShouldNotContain(secret, Case.Insensitive, $"{name} contains a secret");
			}
		}
	}

	private static readonly string[] HostScopes = ["Microsoft.AspNetCore", "System.Net.Http", "SocAlytics", "MailKit"];

	private static bool IsScopeOfHost(string name) =>
		!name.StartsWith("Npgsql", StringComparison.OrdinalIgnoreCase) &&
		HostScopes.Any(scope => name.StartsWith(scope, StringComparison.Ordinal));

	[GeneratedRegex(@"/invitations/accept#token=([A-Za-z0-9_-]+)")]
	private static partial Regex LinkPattern();
}
